using System.Text.Json;

namespace KeySecBox;

public partial class VaultService
{
    // 返回新条目 Id（正数）；失败返回 -(long)ErrorCodes.X（负数），
    // 原因同 AddCategory：Id 与错误码均为正数区间，取负才能区分。
    public long AddEntry(IEnumerable<long> categoryIds, string account, string password, string note)
    {
        if (!S.Unlocked) return -(long)ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return -(long)ErrorCodes.Generic;

        var catIds = NormalizeCategoryIds(categoryIds);
        if (catIds.Count == 0)
            catIds.Add(VaultStore.UncatId);

        long id = S.NextEntryId++;
        var meta = new EntryMeta
        {
            Id = id,
            CatIds = catIds,
            Account = account ?? "",
            Note = note ?? ""
        };
        S.Metas[id] = meta;

        foreach (var cid in catIds)
        {
            if (!S.CatIndex.ContainsKey(cid))
                S.CatIndex[cid] = new List<long>();
            S.CatIndex[cid].Add(id);
        }

        S.SecretCache[id] = new EntrySecret
        {
            Account = account ?? "",
            Password = password ?? "",
            Note = note ?? ""
        };
        S.MetaDirty = true;
        Diag("add_entry: id={0} cats={1}", id, catIds.Count);
        return id;
    }

    public ErrorCodes UpdateEntry(long id, IEnumerable<long> categoryIds, string account, string password, string note)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (!S.Metas.ContainsKey(id)) return ErrorCodes.NotFound;

        var meta = S.Metas[id];
        foreach (var cid in meta.CatIds)
        {
            S.CatIndex.GetValueOrDefault(cid)?.Remove(id);
        }

        var newCatIds = NormalizeCategoryIds(categoryIds);
        if (newCatIds.Count == 0)
            newCatIds.Add(VaultStore.UncatId);

        meta.CatIds = newCatIds;
        meta.Account = account ?? "";
        meta.Note = note ?? "";

        foreach (var cid in newCatIds)
        {
            if (!S.CatIndex.ContainsKey(cid))
                S.CatIndex[cid] = new List<long>();
            if (!S.CatIndex[cid].Contains(id))
                S.CatIndex[cid].Add(id);
        }

        S.SecretCache[id] = new EntrySecret
        {
            Account = account ?? "",
            Password = password ?? "",
            Note = note ?? ""
        };
        S.MetaDirty = true;
        Diag("update_entry: id={0}", id);
        return ErrorCodes.Ok;
    }

    public ErrorCodes RemoveEntry(long id)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (!S.Metas.ContainsKey(id)) return ErrorCodes.NotFound;

        var meta = S.Metas[id];
        foreach (var cid in meta.CatIds)
        {
            S.CatIndex.GetValueOrDefault(cid)?.Remove(id);
        }

        S.Metas.Remove(id);
        S.SecretCache.Remove(id);
        S.EntriesLoc.Remove(id);
        S.RecoveryLoc.Remove(id);
        S.RecoveryCache.Remove(id);
        S.AllOrderPins.Remove(id);
        S.MetaDirty = true;
        S.RecoveryDirty = true;
        Diag("remove_entry: id={0}", id);
        return ErrorCodes.Ok;
    }

    public ErrorCodes MoveEntry(long id, long categoryId, long newPos)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (!S.Metas.ContainsKey(id)) return ErrorCodes.NotFound;
        if (!S.CatIndex.ContainsKey(categoryId)) return ErrorCodes.NotFound;

        var list = S.CatIndex[categoryId];
        list.Remove(id);
        newPos = Math.Clamp(newPos, 0, list.Count);
        list.Insert((int)newPos, id);
        S.MetaDirty = true;
        return ErrorCodes.Ok;
    }

    // 按给定的完整顺序保存「全部」视图排序。
    // 旧的 MoveAllEntry 只 pin 被移动的条目，其余条目仍按默认顺序重排，
    // 多次移动或增删条目后 pin 位置会漂移，导致排序结果回跳。
    // 改为整体全量保存，顺序完全由调用方确定。
    public ErrorCodes SetAllOrder(IEnumerable<long> orderedIds)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;

        S.AllOrderPins.Clear();
        long pos = 0;
        foreach (var id in orderedIds)
        {
            if (S.Metas.ContainsKey(id))
                S.AllOrderPins[id] = pos++;
        }
        S.MetaDirty = true;
        Diag("set_all_order: count={0}", S.AllOrderPins.Count);
        return ErrorCodes.Ok;
    }

    public EntryDetail? GetEntry(long id)
    {
        if (!S.Unlocked) return null;
        if (!S.Metas.ContainsKey(id)) return null;

        var meta = S.Metas[id];
        string password = "";

        if (S.SecretCache.TryGetValue(id, out var cached))
        {
            password = cached.Password;
        }
        else if (S.EntriesLoc.TryGetValue(id, out var loc))
        {
            var (_, _, pwNonce, pwCipher) = _binary.ParseEntryRecord(S.EntriesFile, (long)loc.Offset);
            try
            {
                var pwBytes = _crypto.Decrypt(S.Key, pwNonce, pwCipher);
                password = System.Text.Encoding.UTF8.GetString(pwBytes);
            }
            catch { return null; }
        }

        return new EntryDetail
        {
            Id = id,
            CategoryIds = meta.CatIds.ToList(),
            Account = meta.Account,
            Password = password,
            Note = meta.Note
        };
    }

    public ErrorCodes SetRecovery(long id, List<string> keys)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (!S.Metas.ContainsKey(id)) return ErrorCodes.NotFound;

        if (keys.Count == 0)
        {
            S.RecoveryCache.Remove(id);
            S.RecoveryLoc.Remove(id);
        }
        else
        {
            S.RecoveryCache[id] = keys;
        }
        S.RecoveryDirty = true;
        return ErrorCodes.Ok;
    }

    // 返回副本：调用方常在此结果上 Add/Remove 后再 SetRecovery 回写，
    // 若直接暴露内部 List，未提交的输入框内容会污染内存中的恢复密钥缓存。
    public List<string> GetRecovery(long id)
    {
        if (!S.Unlocked) return new List<string>();
        if (!S.Metas.ContainsKey(id)) return new List<string>();
        var keys = GetRecoveryInternal(id);
        return keys == null ? new List<string>() : new List<string>(keys);
    }

    private List<string>? GetRecoveryInternal(long id)
    {
        if (S.RecoveryCache.TryGetValue(id, out var cached))
            return cached;

        if (!S.RecoveryLoc.TryGetValue(id, out var loc))
            return null;

        var p = (int)loc.Offset;
        _binary.GetI64(S.RecoveryFile, ref p);
        var nonce = _binary.GetBytes(S.RecoveryFile, ref p, 12);
        uint len = _binary.GetU32(S.RecoveryFile, ref p);
        var blob = _binary.GetBytes(S.RecoveryFile, ref p, (int)len);

        var plain = DecryptBlob(nonce, blob);
        if (plain == null) return null;

        using var doc = JsonDocument.Parse(plain);
        var keys = new List<string>();
        foreach (var e in doc.RootElement.EnumerateArray())
            keys.Add(e.GetString() ?? "");
        return keys;
    }

    private List<long> NormalizeCategoryIds(IEnumerable<long> categoryIds)
    {
        var result = new List<long>();
        foreach (var cid in categoryIds.Distinct())
        {
            if (S.Categories.ContainsKey(cid))
                result.Add(cid);
        }
        return result;
    }
}