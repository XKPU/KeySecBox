using System.Security.Cryptography;

namespace KeySecBox;

public class VaultStore : IDisposable
{
    public const long UncatId = 0;
    public const string UncatName = "未分类";
    public const uint Pbkdf2Iterations = 600000;
    public const string MasterCheck = "KSX4-MASTER-OK";

    public string BasePath { get; set; } = string.Empty;

    public bool Unlocked { get; set; }
    public bool MetaDirty { get; set; }
    public bool RecoveryDirty { get; set; }
    public bool LegacyMode { get; set; }

    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public uint Iterations { get; set; } = Pbkdf2Iterations;
    public byte[] Key { get; set; } = Array.Empty<byte>();
    public byte[] ChkNonce { get; set; } = Array.Empty<byte>();
    public byte[] ChkBlob { get; set; } = Array.Empty<byte>();

    public bool Diag { get; set; }

    public Dictionary<long, Category> Categories { get; set; } = new();
    public Dictionary<long, List<long>> CatIndex { get; set; } = new();
    public List<long> CatOrder { get; set; } = new();

    public Dictionary<long, EntryMeta> Metas { get; set; } = new();
    public Dictionary<long, EntrySecret> SecretCache { get; set; } = new();

    public Dictionary<long, DataLoc> EntriesLoc { get; set; } = new();
    public byte[] EntriesFile { get; set; } = Array.Empty<byte>();

    public Dictionary<long, DataLoc> RecoveryLoc { get; set; } = new();
    public byte[] RecoveryFile { get; set; } = Array.Empty<byte>();
    public Dictionary<long, List<string>> RecoveryCache { get; set; } = new();

    public long NextCatId { get; set; } = 1;
    public long NextEntryId { get; set; } = 1;

    public Dictionary<long, long> AllOrderPins { get; set; } = new();

    public bool IsDirty => MetaDirty || RecoveryDirty || SecretCache.Count > 0;

    public void ClearSensitive()
    {
        if (Key.Length > 0)
        {
            CryptographicOperations.ZeroMemory(Key);
            Key = Array.Empty<byte>();
        }
        foreach (var secret in SecretCache.Values)
        {
            secret.Password = string.Empty;
        }
        SecretCache.Clear();
        foreach (var keys in RecoveryCache.Values)
        {
            keys.Clear();
        }
        RecoveryCache.Clear();
        GC.Collect();
    }

    public void Dispose()
    {
        ClearSensitive();
    }
}