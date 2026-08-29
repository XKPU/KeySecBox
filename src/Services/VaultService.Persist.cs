using System.Text;

namespace KeySecBox;

public partial class VaultService
{
    public ErrorCodes Save()
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;

        return WriteAllFiles();
    }

    public bool GetDiagnostics() => S.Diag;

    public void SetDiagnostics(bool enabled)
    {
        S.Diag = enabled;
        _diag.Initialize(S.BasePath, enabled);
    }

    private ErrorCodes WriteAllFiles()
    {
        try
        {
            if (!WritePrefs()) return ErrorCodes.IO;
            if (!WriteMaster()) return ErrorCodes.IO;
            if (!WriteCats()) return ErrorCodes.IO;
            if (!WriteMap()) return ErrorCodes.IO;
            if (!WriteEntries()) return ErrorCodes.IO;
            if (!WriteRecovery()) return ErrorCodes.IO;
            S.MetaDirty = false;
            S.RecoveryDirty = false;
            S.SecretCache.Clear();
            Diag("save: OK");
            return ErrorCodes.Ok;
        }
        catch { return ErrorCodes.IO; }
    }

    private void LoadPrefs()
    {
        var text = _fileIO.ReadAllText(PrefsPath);
        if (text != null)
        {
            try { S.Diag = _json.DeserializePrefs(text); } catch { }
        }
    }

    private bool LoadCats()
    {
        var text = _fileIO.ReadAllText(CatPath);
        if (text == null) return false;
        var categories = _json.DeserializeCategories(text);
        S.Categories.Clear();
        S.CatOrder.Clear();
        foreach (var cat in categories)
        {
            if (S.Categories.ContainsKey(cat.Id)) continue;
            S.Categories[cat.Id] = cat;
            S.CatOrder.Add(cat.Id);
            if (cat.Id >= S.NextCatId) S.NextCatId = cat.Id + 1;
        }
        EnsureUncat();
        foreach (var cid in S.Categories.Keys)
            S.CatIndex.TryAdd(cid, new List<long>());
        Diag("load_cats: count={0}", S.Categories.Count);
        return true;
    }

    private bool LoadMap()
    {
        var text = _fileIO.ReadAllText(MapPath);
        if (text == null) return false;
        var (nextCatId, nextEntryId, catIndex, entries, pins) = _json.DeserializeMap(text);
        S.NextCatId = nextCatId;
        S.NextEntryId = nextEntryId;
        S.Metas = entries;
        S.AllOrderPins = pins;

        foreach (var cid in S.Categories.Keys)
            S.CatIndex[cid] = catIndex.GetValueOrDefault(cid, new List<long>());

        FixCatIndexConsistency();
        Diag("load_map: entries={0}", S.Metas.Count);
        return true;
    }

    private void FixCatIndexConsistency()
    {
        var member = new Dictionary<long, HashSet<long>>();
        foreach (var (eid, meta) in S.Metas)
        {
            var catIds = meta.CatIds.Count > 0 ? meta.CatIds : new List<long> { VaultStore.UncatId };
            foreach (var cid in catIds)
            {
                if (!member.ContainsKey(cid)) member[cid] = new HashSet<long>();
                member[cid].Add(eid);
            }
        }

        foreach (var (cid, entryList) in S.CatIndex)
        {
            if (!member.TryGetValue(cid, out var validSet))
            {
                S.CatIndex[cid] = new List<long>();
                continue;
            }

            var valid = entryList.Where(eid => validSet.Contains(eid)).ToList();
            var rest = validSet.Where(eid => !valid.Contains(eid)).OrderBy(e => e).ToList();
            valid.AddRange(rest);
            S.CatIndex[cid] = valid;
        }
    }

    private bool LoadEntries()
    {
        var data = _fileIO.ReadAllBytes(EntriesPath);
        if (data == null) return false;
        S.EntriesFile = data;

        var records = _binary.ScanEntriesFile(data);
        S.EntriesLoc.Clear();
        foreach (var (id, offset, total) in records)
        {
            S.EntriesLoc[id] = new DataLoc { Offset = (ulong)offset, Total = (uint)total };

            if (S.Metas.TryGetValue(id, out var meta))
            {
                var (account, note, _, _) = _binary.ParseEntryRecord(data, offset);
                if (account != null) meta.Account = account;
                if (note != null) meta.Note = note;
            }
        }

        Diag("load_entries: records={0}", S.EntriesLoc.Count);
        return true;
    }

    private bool LoadRecovery()
    {
        if (!_fileIO.FileExists(RecoveryPath))
        {
            S.RecoveryFile = Array.Empty<byte>();
            S.RecoveryLoc.Clear();
            return true;
        }

        var data = _fileIO.ReadAllBytes(RecoveryPath);
        if (data == null) return false;
        S.RecoveryFile = data;

        var records = _binary.ScanRecoveryFile(data);
        S.RecoveryLoc.Clear();
        foreach (var (id, offset, total) in records)
            S.RecoveryLoc[id] = new DataLoc { Offset = (ulong)offset, Total = (uint)total };

        Diag("load_recovery: records={0}", S.RecoveryLoc.Count);
        return true;
    }

    private bool WritePrefs()
    {
        var text = _json.SerializePrefs(S.Diag);
        return _fileIO.AtomicWriteAllText(PrefsPath, text);
    }

    private bool WriteMaster()
    {
        var nonce = _crypto.GenerateRandomBytes(12);
        var chkPlain = Encoding.UTF8.GetBytes(VaultStore.MasterCheck);

        Span<byte> output = new byte[chkPlain.Length + 16];
        _crypto.Encrypt(S.Key, chkPlain, nonce, output);
        S.ChkNonce = nonce;
        S.ChkBlob = output.ToArray();

        var data = _binary.BuildMasterFile(S.Salt, S.Iterations, S.ChkNonce, S.ChkBlob);
        return _fileIO.AtomicWriteAllBytes(MasterPath, data);
    }

    private bool WriteCats()
    {
        var text = _json.SerializeCategories(S.Categories.Values, S.CatOrder);
        return _fileIO.AtomicWriteAllText(CatPath, text);
    }

    private bool WriteMap()
    {
        var text = _json.SerializeMap(S.NextCatId, S.NextEntryId, S.CatIndex, S.Metas, S.AllOrderPins);
        return _fileIO.AtomicWriteAllText(MapPath, text);
    }

    private bool WriteEntries()
    {
        if (S.SecretCache.Count == 0) return true;

        var ids = S.Metas.Keys.OrderBy(id => id).ToList();
        var entryRecords = new List<byte[]>();

        foreach (var id in ids)
        {
            if (S.SecretCache.TryGetValue(id, out var secret))
            {
                var rec = BuildEntryRecordFromSecret(id, secret);
                if (rec == null) return false;
                entryRecords.Add(rec);
            }
            else if (S.EntriesLoc.TryGetValue(id, out var loc))
            {
                var rec = S.EntriesFile.AsSpan((int)loc.Offset, (int)loc.Total).ToArray();
                entryRecords.Add(rec);
            }
            else
            {
                return false;
            }
        }

        var data = _binary.BuildEntriesFile(entryRecords);
        if (!_fileIO.AtomicWriteAllBytes(EntriesPath, data)) return false;

        S.EntriesFile = data;
        S.EntriesLoc.Clear();

        var scanned = _binary.ScanEntriesFile(data);
        foreach (var (id, offset, total) in scanned)
            S.EntriesLoc[id] = new DataLoc { Offset = (ulong)offset, Total = (uint)total };

        Diag("write_entries: records={0}", S.EntriesLoc.Count);
        return true;
    }

    private byte[]? BuildEntryRecordFromSecret(long id, EntrySecret secret)
    {
        var pwBytes = Encoding.UTF8.GetBytes(secret.Password);
        var combined = _crypto.Encrypt(S.Key, pwBytes);
        var pwNonce = combined[..12];
        var pwCipher = combined[12..];
        return _binary.BuildEntryRecord(id, secret.Account, secret.Note, pwNonce, pwCipher);
    }

    private bool WriteRecovery()
    {
        if (!S.RecoveryDirty) return true;

        var ordered = S.RecoveryLoc
            .Select(kv => (kv.Key, Offset: kv.Value.Offset))
            .OrderBy(x => x.Offset)
            .ToList();

        var records = new List<byte[]>();
        foreach (var (id, _) in ordered)
        {
            if (S.RecoveryCache.TryGetValue(id, out var keys))
            {
                var rec = BuildRecoveryRecordFromKeys(id, keys);
                if (rec == null) return false;
                records.Add(rec);
            }
            else
            {
                var loc = S.RecoveryLoc[id];
                records.Add(S.RecoveryFile.AsSpan((int)loc.Offset, (int)loc.Total).ToArray());
            }
        }

        foreach (var (id, keys) in S.RecoveryCache)
        {
            if (S.RecoveryLoc.ContainsKey(id)) continue;
            var rec = BuildRecoveryRecordFromKeys(id, keys);
            if (rec == null) return false;
            records.Add(rec);
        }

        var data = _binary.BuildRecoveryFile(records);
        if (!_fileIO.AtomicWriteAllBytes(RecoveryPath, data)) return false;

        S.RecoveryFile = data;
        S.RecoveryLoc = _binary.ScanRecoveryFile(data)
            .ToDictionary(x => x.id, x => new DataLoc { Offset = (ulong)x.offset, Total = (uint)x.total });
        S.RecoveryCache.Clear();
        S.RecoveryDirty = false;
        Diag("write_recovery: records={0}", S.RecoveryLoc.Count);
        return true;
    }

    private byte[]? BuildRecoveryRecordFromKeys(long id, List<string> keys)
    {
        var plain = System.Text.Json.JsonSerializer.Serialize(keys);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var combined = _crypto.Encrypt(S.Key, plainBytes);
        var nonce = combined[..12];
        var blob = combined[12..];
        return _binary.BuildRecoveryRecord(id, nonce, blob);
    }
}