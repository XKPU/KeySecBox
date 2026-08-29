namespace KeySecBox;

public interface ILegacyVaultService
{
    ErrorCodes OpenLegacy(string legacyDir, string masterPassword, out IVaultService? vault);
}

public class LegacyVaultService : ILegacyVaultService
{
    private readonly ICryptoService _crypto;
    private readonly IFileIOService _fileIO;
    private readonly IBinaryFormatService _binary;
    private readonly IJsonSerializationService _json;
    private readonly IDiagnosticService _diag;

    private static readonly byte[] SettingsMagic = System.Text.Encoding.ASCII.GetBytes("KSX3");

    public LegacyVaultService(
        ICryptoService crypto, IFileIOService fileIO,
        IBinaryFormatService binary, IJsonSerializationService json, IDiagnosticService diag)
    {
        _crypto = crypto;
        _fileIO = fileIO;
        _binary = binary;
        _json = json;
        _diag = diag;
    }

    public ErrorCodes OpenLegacy(string legacyDir, string masterPassword, out IVaultService? vault)
    {
        vault = null;
        var settingsPath = legacyDir + ".settings";
        var indexPath = legacyDir + ".index";
        var dataPath = legacyDir + ".data";
        var recoveryPath = legacyDir + ".recovery";

        if (!_fileIO.FileExists(settingsPath)) return ErrorCodes.NoVault;

        var settings = _fileIO.ReadAllBytes(settingsPath);
        if (settings == null || settings.Length < 40) return ErrorCodes.IO;
        if (!settings.AsSpan(0, 4).SequenceEqual(SettingsMagic)) return ErrorCodes.IO;

        var vs = new VaultService(_crypto, _fileIO, _binary, _json, _diag);
        var s = vs.S;

        int p = 4;
        uint ver = _binary.GetU32(settings, ref p);
        s.Salt = _binary.GetBytes(settings, ref p, 16);

        if (ver >= 2)
        {
            if (p < settings.Length) p++; // skip kdf byte
            s.Iterations = _binary.GetU32(settings, ref p);
        }
        else
        {
            s.Iterations = _binary.GetU32(settings, ref p);
        }

        s.ChkNonce = _binary.GetBytes(settings, ref p, 12);
        uint cLen = _binary.GetU32(settings, ref p);
        s.ChkBlob = _binary.GetBytes(settings, ref p, (int)cLen);

        var key = new byte[32];
        _crypto.DeriveKey(masterPassword, s.Salt, s.Iterations, key);

        var chkBytes = new byte[3]; // "KSX3-OK" length
        var ok = _crypto.TryDecrypt(key, s.ChkNonce, s.ChkBlob, chkBytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        if (!ok) return ErrorCodes.WrongPassword;

        s.Key = key;
        s.BasePath = legacyDir;
        s.LegacyMode = true;
        s.Unlocked = true;

        LoadLegacyIndex(vs, indexPath);
        LoadLegacyData(vs, dataPath);
        LoadLegacyRecovery(vs, recoveryPath);

        vault = vs;
        return ErrorCodes.Ok;
    }

    private void LoadLegacyIndex(VaultService vs, string indexPath)
    {
        var text = _fileIO.ReadAllText(indexPath);
        if (text == null) return;
        var s = vs.S;

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.TryGetProperty("nextCatId", out var nci)) s.NextCatId = nci.GetInt64();
        if (root.TryGetProperty("nextEntryId", out var nei)) s.NextEntryId = nei.GetInt64();

        if (root.TryGetProperty("categories", out var cats))
        {
            foreach (var c in cats.EnumerateArray())
            {
                var cat = new Category
                {
                    Id = c.GetProperty("id").GetInt64(),
                    Name = c.GetProperty("name").GetString() ?? ""
                };
                s.Categories[cat.Id] = cat;
                s.CatOrder.Add(cat.Id);
            }
        }

        if (root.TryGetProperty("entries", out var entries))
        {
            foreach (var e in entries.EnumerateArray())
            {
                var id = e.GetProperty("id").GetInt64();
                var meta = new EntryMeta { Id = id, CatIds = new List<long>() };

                if (e.TryGetProperty("catId", out var cid))
                    meta.CatIds.Add(cid.GetInt64());
                if (e.TryGetProperty("cats", out var multi))
                    foreach (var c in multi.EnumerateArray())
                        meta.CatIds.Add(c.GetInt64());
                if (e.TryGetProperty("catIds", out var multi2))
                    foreach (var c in multi2.EnumerateArray())
                        meta.CatIds.Add(c.GetInt64());

                if (meta.CatIds.Count == 0) meta.CatIds.Add(VaultStore.UncatId);
                s.Metas[id] = meta;
            }
        }

        vs.EnsureUncat();
    }

    private void LoadLegacyData(VaultService vs, string dataPath)
    {
        var data = _fileIO.ReadAllBytes(dataPath);
        if (data == null) return;
        var s = vs.S;
        s.EntriesFile = data;

        int p = 0;
        if (data.Length < 8) return;
        p = 4;
        uint ver = _binary.GetU32(data, ref p);

        while (p < data.Length)
        {
            long recStart = p;
            long id = _binary.GetI64(data, ref p);
            uint len = _binary.GetU32(data, ref p);
            if (p + len > data.Length) break;

            s.EntriesLoc[id] = new DataLoc { Offset = (ulong)recStart, Total = (uint)(p + len - recStart) };

            if (s.Metas.TryGetValue(id, out var meta))
            {
                var nonce = _binary.GetBytes(data, ref p, 12);
                int cipherLen = (int)len - 12;
                var cipher = _binary.GetBytes(data, ref p, cipherLen);
                
                try
                {
                    var plain = _crypto.Decrypt(s.Key, nonce, cipher);
                    var plainStr = System.Text.Encoding.UTF8.GetString(plain);
                    var parts = plainStr.Split('\n');
                    if (parts.Length >= 1) meta.Account = parts[0];
                    if (parts.Length >= 2) meta.Note = parts.Length >= 3 ? parts[2] : "";
                }
                catch { }
            }
            else
            {
                p += (int)len;
            }
        }
    }

    private void LoadLegacyRecovery(VaultService vs, string path)
    {
        if (!_fileIO.FileExists(path)) return;

        var data = _fileIO.ReadAllBytes(path);
        if (data == null) return;
        var s = vs.S;
        s.RecoveryFile = data;

        var records = _binary.ScanRecoveryFile(data);
        foreach (var (id, offset, total) in records)
            s.RecoveryLoc[id] = new DataLoc { Offset = (ulong)offset, Total = (uint)total };
    }
}

internal static class VaultServiceExtensions
{
    public static void EnsureUncat(this VaultService vs)
    {
        var s = vs.S;
        if (!s.Categories.ContainsKey(VaultStore.UncatId))
        {
            s.Categories[VaultStore.UncatId] = new Category { Id = VaultStore.UncatId, Name = VaultStore.UncatName };
            s.CatIndex.TryAdd(VaultStore.UncatId, new List<long>());
        }
        if (!s.CatOrder.Contains(VaultStore.UncatId))
            s.CatOrder.Insert(0, VaultStore.UncatId);
    }
}