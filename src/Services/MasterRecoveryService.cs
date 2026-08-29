namespace KeySecBox;

public interface IMasterRecoveryService
{
    RecoveryConfig GetConfig();
    ErrorCodes Save(string masterPassword, string? backupPassword, bool useSystem, bool keepBackup = false);
    string? RecoverByBackup(string backupPassword);
    string? RecoverBySystem();
}

public class MasterRecoveryService : IMasterRecoveryService
{
    private readonly ICryptoService _crypto;
    private readonly IFileIOService _fileIO;
    private static readonly byte[] RecoveryMagic = System.Text.Encoding.ASCII.GetBytes("KSXRv2");
    private static readonly string RecoveryFile = AppPaths.MasterRecoveryFile;

    public MasterRecoveryService(ICryptoService crypto, IFileIOService fileIO)
    {
        _crypto = crypto;
        _fileIO = fileIO;
    }

    public RecoveryConfig GetConfig()
    {
        var data = _fileIO.ReadAllBytes(RecoveryFile);
        if (data == null || data.Length < 8)
            return new RecoveryConfig();

        var sp = data.AsSpan();
        if (!sp[..6].SequenceEqual(RecoveryMagic))
            return new RecoveryConfig();

        bool hasBackup = sp[6] == 1;
        bool hasSystem = sp[7] == 1;
        return new RecoveryConfig
        {
            HasBackup = hasBackup,
            HasSystem = hasSystem,
            IsReady = hasBackup || hasSystem,
            Any = hasBackup || hasSystem
        };
    }

    public ErrorCodes Save(string masterPassword, string? backupPassword, bool useSystem, bool keepBackup = false)
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(RecoveryMagic);
            ms.WriteByte(backupPassword != null ? (byte)1 : (byte)0);
            ms.WriteByte(useSystem ? (byte)1 : (byte)0);

            if (backupPassword != null)
            {
                var salt = _crypto.GenerateRandomBytes(16);
                ms.Write(salt);
                var key = _crypto.DeriveKey(backupPassword, salt, 600000);
                var plain = System.Text.Encoding.UTF8.GetBytes(masterPassword);
                var cipher = _crypto.Encrypt(key, plain);
                ms.Write(cipher);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }

            if (useSystem)
            {
                var plain = System.Text.Encoding.UTF8.GetBytes(masterPassword);
                var protected_data = DpapiNative.Protect(plain);
                Span<byte> len = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, protected_data.Length);
                ms.Write(len);
                ms.Write(protected_data);
            }

            return _fileIO.AtomicWriteAllBytes(RecoveryFile, ms.ToArray())
                ? ErrorCodes.Ok : ErrorCodes.IO;
        }
        catch { return ErrorCodes.IO; }
    }

    public string? RecoverByBackup(string backupPassword)
    {
        var data = _fileIO.ReadAllBytes(RecoveryFile);
        if (data == null || data.Length < 16) return null;

        var sp = data.AsSpan();
        if (!sp[..6].SequenceEqual(RecoveryMagic)) return null;
        if (sp[6] != 1) return null;

        int p = 8;
        var salt = sp.Slice(p, 16).ToArray(); p += 16;
        var key = _crypto.DeriveKey(backupPassword, salt, 600000);

        try
        {
            var cipherWithTag = sp[p..].ToArray();
            var cipherLen = cipherWithTag.Length - 16;
            var plain = new byte[cipherLen];
            if (!_crypto.TryDecrypt(key, cipherWithTag[..12], cipherWithTag[12..], plain))
                return null;
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    public string? RecoverBySystem()
    {
        var data = _fileIO.ReadAllBytes(RecoveryFile);
        if (data == null || data.Length < 12) return null;

        var sp = data.AsSpan();
        if (!sp[..6].SequenceEqual(RecoveryMagic)) return null;
        if (sp[7] != 1) return null;

        int p = 8;
        if (sp[6] == 1)
        {
            p += 16;
            uint cLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sp[p..]);
            p += 4 + (int)cLen + 16;
        }

        uint sysLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sp[p..]);
        p += 4;
        var protected_data = sp.Slice(p, (int)sysLen).ToArray();

        var plain = DpapiNative.Unprotect(protected_data);
        return plain != null ? System.Text.Encoding.UTF8.GetString(plain) : null;
    }
}