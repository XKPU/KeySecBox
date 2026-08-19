using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeySecBox;

/// <summary>
/// 主密码恢复（忘记密码时的取回手段）。主密码以加密副本落盘，明文永不直接存储：
///   - 备用密码：PBKDF2-HMAC-SHA256 派生密钥，AES-GCM 加密主密码。
///   - 系统解锁（PIN/指纹/人脸）：DPAPI CurrentUser 保护，绑定当前 Windows 账户。
/// 两种方式可同时启用；都不需要时可移除恢复记录文件。
/// </summary>
internal static class RecoveryManager
{
    private const int PwdIterations = 600_000; // 与保险库 KDF 同量级，防爆破
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const string Magic = "KSXR2";
    private const string LegacyMagic = "KSXR";

    #region 数据模型

    public sealed class Config
    {
        public bool HasBackup { get; init; }
        public bool HasSystem { get; init; }
        /// <summary>记录为 v2（含内部 RK 保管）：改密后可免重输备用密码无缝延续既有恢复方式。</summary>
        public bool IsReady { get; init; }
        public bool Any => HasBackup || HasSystem;
    }

    private sealed class BackupBlob
    {
        public int iterations { get; set; }
        public string salt { get; set; } = "";
        public string nonce { get; set; } = "";
        public string tag { get; set; } = "";
        public string cipher { get; set; } = "";
    }

    private sealed class SystemBlob
    {
        public string blob { get; set; } = "";
    }

    private sealed class MasterBlob
    {
        public string nonce { get; set; } = "";
        public string tag { get; set; } = "";
        public string cipher { get; set; } = "";
    }

    private sealed class KeeperBlob
    {
        public string blob { get; set; } = "";
    }

    private sealed class Record
    {
        public string magic { get; set; } = Magic;
        /// <summary>DPAPI(RK)：应用内部保管的恢复密钥，改密时用它免备用密码无缝重加密主密码。</summary>
        public KeeperBlob? keeper { get; set; }
        public BackupBlob? backup { get; set; }   // AES-GCM(备用密码派生密钥, RK)
        public SystemBlob? system { get; set; }   // DPAPI(RK)
        public MasterBlob? master { get; set; }   // AES-GCM(RK, 主密码)
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = false };

    #endregion

    #region 配置读写

    public static Config GetConfig()
    {
        var rec = Load();
        bool v2 = rec?.keeper != null;
        return new Config
        {
            HasBackup = rec?.backup != null,
            HasSystem = rec?.system != null,
            IsReady = v2
        };
    }

    /// <summary>
    /// 保存恢复记录。
    /// backupPassword 非空 = 用新备用密码包裹恢复密钥；keepBackup = 保持已有备用密码。
    /// useSystem 为 false 则停用系统方式。backupPassword 为空且不 keepBackup 时停用备用密码。
    /// 两者皆停则删除文件。改密后调用方传入新主密码即可，无需重输备用密码。
    /// </summary>
    public static int Save(string masterPassword, string? backupPassword, bool useSystem, bool keepBackup = false)
    {
        if (string.IsNullOrEmpty(masterPassword)) return -1;
        var prev = Load();
        var rec = new Record();

        // 恢复密钥 RK：已有 v2 记录沿用之，新记录才生成；保证各方式指向同一密钥
        byte[] rk;
        if (prev?.keeper != null)
            rk = UnprotectRk(prev) ?? RandomNumberGenerator.GetBytes(KeyBytes);
        else
            rk = RandomNumberGenerator.GetBytes(KeyBytes);

        if (keepBackup)
        {
            // 占位符表示「保持原备用密码」：直接沿用既有包裹（同一 RK 无需变动）
            if (prev?.keeper != null) rec.backup = prev.backup;
            else if (!string.IsNullOrEmpty(backupPassword)) rec.backup = WrapRkBackup(rk, backupPassword);
        }
        else if (!string.IsNullOrEmpty(backupPassword))
            rec.backup = WrapRkBackup(rk, backupPassword);

        if (useSystem)
            rec.system = WrapRkSystem(rk);

        if (rec.backup == null && rec.system == null)
        {
            DeleteFile();
            Array.Clear(rk, 0, rk.Length);
            return 0;
        }

        // 修改备用密码（!keepBackup）时，完整解密既有记录中的主密码再重新加密：
        // 以记录内密文为权威来源，不依赖调用方传入的明文（即使其缺失/错误也保持一致），
        // 同时顺带验证 RK 链可解密（keeper/DPAPI 链损坏时提前暴露而非静默覆盖）。
        string effectiveMaster = masterPassword;
        if (!keepBackup && prev?.keeper != null && prev.master != null)
        {
            var decrypted = UnwrapMaster(prev.master, rk);
            if (decrypted != null) effectiveMaster = decrypted;
            else if (string.IsNullOrEmpty(effectiveMaster)) { Array.Clear(rk, 0, rk.Length); return -1; }
        }

        rec.master = WrapMaster(rk, effectiveMaster);
        rec.keeper = ProtectRk(rk);
        Array.Clear(rk, 0, rk.Length);

        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.MasterRecoveryFile, JsonSerializer.Serialize(rec, JsonOpts));
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>取回主密码。备用密码错误返回 null（GCM tag 校验失败）。兼容新旧(v1/v2)两种记录。</summary>
    public static string? RecoverByBackup(string backupPassword)
    {
        if (string.IsNullOrEmpty(backupPassword)) return null;
        var rec = Load();
        if (rec?.backup is not { } b) return null;

        if (rec.keeper != null)
        {
            var rk = UnwrapRkBackup(b, backupPassword);
            if (rk == null) return null;
            try { return UnwrapMaster(rec.master, rk); }
            finally { Array.Clear(rk, 0, rk.Length); }
        }

        // v1 旧格式：备用密码直接加密主密码
        try
        {
            var salt = Convert.FromBase64String(b.salt);
            var nonce = Convert.FromBase64String(b.nonce);
            var tag = Convert.FromBase64String(b.tag);
            var cipher = Convert.FromBase64String(b.cipher);
            var key = DeriveKey(backupPassword, salt, b.iterations);
            using var gcm = new AesGcm(key, 16);
            var plain = new byte[cipher.Length];
            gcm.Decrypt(nonce, cipher, tag, plain);
            string master = Encoding.UTF8.GetString(plain);
            Array.Clear(key, 0, key.Length);
            Array.Clear(plain, 0, plain.Length);
            return master;
        }
        catch (CryptographicException)
        {
            return null; // 备用密码错误
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通过 Windows 账户保护取回主密码。调用前需先经 UserConsentVerifier 系统验证。兼容新旧(v1/v2)两种记录。</summary>
    public static string? RecoverBySystem()
    {
        var rec = Load();
        if (rec?.system is not { } sys) return null;

        if (rec.keeper != null)
        {
            var rk = UnwrapRkSystem(sys);
            if (rk == null) return null;
            try { return UnwrapMaster(rec.master, rk); }
            finally { Array.Clear(rk, 0, rk.Length); }
        }

        // v1 旧格式：DPAPI 直接保护主密码
        try
        {
            var bytes = CryptUnprotect(Convert.FromBase64String(sys.blob));
            string master = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes, 0, bytes.Length);
            return master;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 加密原语

    private static BackupBlob WrapRkBackup(byte[] rk, string backupPassword)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(backupPassword, salt, PwdIterations);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[rk.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, rk, cipher, tag);
        Array.Clear(key, 0, key.Length);
        return new BackupBlob
        {
            iterations = PwdIterations,
            salt = Convert.ToBase64String(salt),
            nonce = Convert.ToBase64String(nonce),
            tag = Convert.ToBase64String(tag),
            cipher = Convert.ToBase64String(cipher)
        };
    }

    private static byte[]? UnwrapRkBackup(BackupBlob b, string backupPassword)
    {
        try
        {
            var salt = Convert.FromBase64String(b.salt);
            var nonce = Convert.FromBase64String(b.nonce);
            var tag = Convert.FromBase64String(b.tag);
            var cipher = Convert.FromBase64String(b.cipher);
            var key = DeriveKey(backupPassword, salt, b.iterations);
            var rk = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, 16))
                gcm.Decrypt(nonce, cipher, tag, rk);
            Array.Clear(key, 0, key.Length);
            return rk;
        }
        catch (CryptographicException)
        {
            return null; // 备用密码错误
        }
        catch
        {
            return null;
        }
    }

    private static SystemBlob WrapRkSystem(byte[] rk)
    {
        var protected_ = CryptProtect(rk);
        return new SystemBlob { blob = Convert.ToBase64String(protected_) };
    }

    private static byte[]? UnwrapRkSystem(SystemBlob s)
    {
        try { return CryptUnprotect(Convert.FromBase64String(s.blob)); }
        catch { return null; }
    }

    private static KeeperBlob ProtectRk(byte[] rk)
        => new() { blob = Convert.ToBase64String(CryptProtect(rk)) };

    private static byte[]? UnprotectRk(Record rec)
    {
        try { return rec.keeper != null ? CryptUnprotect(Convert.FromBase64String(rec.keeper.blob)) : null; }
        catch { return null; }
    }

    private static MasterBlob WrapMaster(byte[] rk, string masterPassword)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(masterPassword);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(rk, 16))
            gcm.Encrypt(nonce, plain, cipher, tag);
        Array.Clear(plain, 0, plain.Length);
        return new MasterBlob
        {
            nonce = Convert.ToBase64String(nonce),
            tag = Convert.ToBase64String(tag),
            cipher = Convert.ToBase64String(cipher)
        };
    }

    private static string? UnwrapMaster(MasterBlob? m, byte[] rk)
    {
        if (m == null) return null;
        try
        {
            var nonce = Convert.FromBase64String(m.nonce);
            var tag = Convert.FromBase64String(m.tag);
            var cipher = Convert.FromBase64String(m.cipher);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(rk, 16))
                gcm.Decrypt(nonce, cipher, tag, plain);
            string master = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return master;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeyBytes);
    }

    private static Record? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.MasterRecoveryFile)) return null;
            var json = File.ReadAllText(AppPaths.MasterRecoveryFile);
            var rec = JsonSerializer.Deserialize<Record>(json, JsonOpts);
            if (rec == null || (rec.magic != Magic && rec.magic != LegacyMagic)) return null;
            return rec;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteFile()
    {
        try { File.Delete(AppPaths.MasterRecoveryFile); } catch { }
    }

    #endregion

    #region DPAPI (crypt32)

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] CryptProtect(byte[] data)
    {
        var blobIn = StructToBlob(data);
        try
        {
            DATA_BLOB blobOut;
            if (!CryptProtectData(ref blobIn, "KeySecBox master recovery", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0, out blobOut))
                throw new InvalidOperationException("CryptProtectData failed");
            return BlobToBytes(blobOut);
        }
        finally
        {
            LocalFree(blobIn.pbData);
        }
    }

    private static byte[] CryptUnprotect(byte[] data)
    {
        var blobIn = StructToBlob(data);
        try
        {
            DATA_BLOB blobOut;
            if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0, out blobOut))
                throw new InvalidOperationException("CryptUnprotectData failed");
            return BlobToBytes(blobOut);
        }
        finally
        {
            LocalFree(blobIn.pbData);
        }
    }

    private static DATA_BLOB StructToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static byte[] BlobToBytes(DATA_BLOB blob)
    {
        var bytes = new byte[blob.cbData];
        if (blob.cbData > 0) Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        LocalFree(blob.pbData);
        return bytes;
    }

    #endregion
}
