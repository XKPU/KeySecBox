using System.Text;

namespace KeySecBox;

public partial class VaultService
{
    public ErrorCodes Setup(string basePath, string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword)) return ErrorCodes.Generic;
        if (_fileIO.FileExists(MasterPath)) return ErrorCodes.Generic;

        S.BasePath = basePath;

        S.Salt = _crypto.GenerateRandomBytes(16);
        S.Iterations = VaultStore.Pbkdf2Iterations;
        DeriveKey(masterPassword);

        S.ChkNonce = _crypto.GenerateRandomBytes(12);
        var chkPlain = Encoding.UTF8.GetBytes(VaultStore.MasterCheck);
        S.ChkBlob = _crypto.Encrypt(S.Key, chkPlain);

        EnsureUncat();
        // 新建完成后即处于已解锁状态：所有查询/写入接口都以 S.Unlocked 把关，
        // 不置位会导致建库后列表为空、后续新增与保存全部返回 NotUnlocked。
        S.Unlocked = true;
        Diag("setup: OK iter={0}", S.Iterations);
        return WriteAllFiles();
    }

    public ErrorCodes Open(string basePath, string masterPassword)
    {
        S.BasePath = basePath;
        _diag.Initialize(basePath, false);

        if (!_fileIO.FileExists(MasterPath))
        {
            var legacySettings = basePath + ".settings";
            if (_fileIO.FileExists(legacySettings))
                return ErrorCodes.Legacy;
            return ErrorCodes.NoVault;
        }

        LoadPrefs();
        _diag.Initialize(basePath, S.Diag);

        var masterData = _fileIO.ReadAllBytes(MasterPath);
        if (masterData == null) return ErrorCodes.IO;
        try
        {
            var (salt, iterations, chkNonce, chkBlob) = _binary.ParseMasterFile(masterData);
            S.Salt = salt;
            S.Iterations = iterations;
            S.ChkNonce = chkNonce;
            S.ChkBlob = chkBlob;
        }
        catch { return ErrorCodes.IO; }

        if (!DeriveKey(masterPassword)) return ErrorCodes.Generic;
        if (!VerifyCheckBlock()) return ErrorCodes.WrongPassword;

        if (!LoadCats()) return ErrorCodes.IO;
        if (!LoadMap()) return ErrorCodes.IO;
        if (!LoadEntries()) return ErrorCodes.IO;
        if (!LoadRecovery()) return ErrorCodes.IO;

        S.Unlocked = true;
        Diag("open: OK entries={0}", S.Metas.Count);
        return ErrorCodes.Ok;
    }

    public ErrorCodes ChangePassword(string newMasterPassword)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (string.IsNullOrEmpty(newMasterPassword)) return ErrorCodes.Generic;

        var oldSalt = S.Salt.ToArray();
        var oldKey = S.Key.ToArray();

        var entryBackup = _fileIO.ReadAllBytes(EntriesPath);
        var recoveryBackup = _fileIO.ReadAllBytes(RecoveryPath);

        var plainEntries = new Dictionary<long, string>();
        foreach (var (id, loc) in S.EntriesLoc)
        {
            var (_, _, pwNonce, pwCipher) = _binary.ParseEntryRecord(S.EntriesFile, (long)loc.Offset);
            var pwBytes = _crypto.Decrypt(S.Key, pwNonce, pwCipher);
            plainEntries[id] = Encoding.UTF8.GetString(pwBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pwBytes);
        }

        var plainRecoveries = new Dictionary<long, List<string>>();
        foreach (var (id, _) in S.RecoveryLoc)
        {
            var keys = GetRecoveryInternal(id);
            if (keys != null) plainRecoveries[id] = keys;
        }

        try
        {
            S.Salt = _crypto.GenerateRandomBytes(16);
            if (!DeriveKey(newMasterPassword))
                throw new Exception("derive failed");

            foreach (var (id, pw) in plainEntries)
            {
                S.SecretCache[id] = new EntrySecret
                {
                    Account = S.Metas.TryGetValue(id, out var m) ? m.Account : "",
                    Password = pw,
                    Note = S.Metas.TryGetValue(id, out var n) ? n.Note : ""
                };
            }
            S.MetaDirty = true;

            foreach (var (id, keys) in plainRecoveries)
            {
                S.RecoveryCache[id] = keys;
            }
            S.RecoveryDirty = true;

            return WriteAllFiles();
        }
        catch
        {
            S.Salt = oldSalt;
            S.Key = oldKey;
            if (entryBackup != null) _fileIO.AtomicWriteAllBytes(EntriesPath, entryBackup);
            if (recoveryBackup != null) _fileIO.AtomicWriteAllBytes(RecoveryPath, recoveryBackup);
            return ErrorCodes.IO;
        }
    }

    public ErrorCodes VerifyPassword(string masterPassword)
    {
        if (S.Unlocked && !S.LegacyMode) return ErrorCodes.Ok;

        var salt = S.Salt;
        var iterations = S.Iterations;
        var chkNonce = S.ChkNonce;
        var chkBlob = S.ChkBlob;
        var basePath = S.BasePath;

        if (salt.Length == 0)
        {
            if (!_fileIO.FileExists(MasterPath)) return ErrorCodes.NoVault;
            var masterData = _fileIO.ReadAllBytes(MasterPath);
            if (masterData == null) return ErrorCodes.IO;
            try
            {
                var parsed = _binary.ParseMasterFile(masterData);
                salt = parsed.salt;
                iterations = parsed.iterations;
                chkNonce = parsed.chkNonce;
                chkBlob = parsed.chkBlob;
            }
            catch { return ErrorCodes.IO; }
        }

        var key = new byte[32];
        _crypto.DeriveKey(masterPassword, salt, iterations, key);

        try
        {
            var chkBytes = new byte[VaultStore.MasterCheck.Length];
            if (!_crypto.TryDecrypt(key, chkNonce, chkBlob, chkBytes))
                return ErrorCodes.WrongPassword;
            var chk = Encoding.UTF8.GetString(chkBytes);
            return chk == VaultStore.MasterCheck ? ErrorCodes.Ok : ErrorCodes.WrongPassword;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    private bool DeriveKey(string password)
    {
        var key = new byte[32];
        _crypto.DeriveKey(password, S.Salt, S.Iterations, key);
        if (S.Key.Length > 0)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(S.Key);
        S.Key = key;
        return true;
    }

    private bool VerifyCheckBlock()
    {
        var chkBytes = new byte[VaultStore.MasterCheck.Length];
        if (!_crypto.TryDecrypt(S.Key, S.ChkNonce, S.ChkBlob, chkBytes))
            return false;
        return Encoding.UTF8.GetString(chkBytes) == VaultStore.MasterCheck;
    }

    private void EnsureUncat()
    {
        if (!S.Categories.ContainsKey(VaultStore.UncatId))
        {
            S.Categories[VaultStore.UncatId] = new Category { Id = VaultStore.UncatId, Name = VaultStore.UncatName };
            S.CatIndex[VaultStore.UncatId] = new List<long>();
        }
        if (!S.CatOrder.Contains(VaultStore.UncatId))
        {
            S.CatOrder.Insert(0, VaultStore.UncatId);
        }
    }

    internal string? EncryptString(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var result = _crypto.Encrypt(S.Key, plainBytes);
        return Convert.ToBase64String(result);
    }

    internal (byte[] nonce, byte[] blob) EncryptBlob(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var combined = _crypto.Encrypt(S.Key, plainBytes);
        var nonce = combined[..12];
        var blob = combined[12..];
        return (nonce, blob);
    }

    internal string? DecryptBlob(byte[] nonce, byte[] blob)
    {
        try
        {
            var plain = _crypto.Decrypt(S.Key, nonce, blob);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }
}