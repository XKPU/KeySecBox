namespace KeySecBox;

public partial class VaultService
{
    public List<EntrySummary> QueryAll()
    {
        if (!S.Unlocked) return new List<EntrySummary>();

        var order = BuildAllWithPins();
        return order.Select(id =>
        {
            var meta = S.Metas.GetValueOrDefault(id);
            if (meta == null) return null;
            return new EntrySummary
            {
                Id = meta.Id,
                CategoryIds = meta.CatIds.ToList(),
                Account = meta.Account,
                Note = meta.Note
            };
        }).Where(e => e != null).Cast<EntrySummary>().ToList();
    }

    public List<EntrySummary> QueryCategory(long categoryId)
    {
        if (!S.Unlocked) return new List<EntrySummary>();
        if (!S.CatIndex.TryGetValue(categoryId, out var entryIds))
            return new List<EntrySummary>();

        return entryIds
            .Where(id => S.Metas.ContainsKey(id))
            .Select(id =>
            {
                var meta = S.Metas[id];
                return new EntrySummary
                {
                    Id = meta.Id,
                    CategoryId = categoryId,
                    CategoryIds = meta.CatIds.ToList(),
                    Account = meta.Account,
                    Note = meta.Note
                };
            }).ToList();
    }

    public List<EntrySummary> Search(string keyword)
    {
        if (!S.Unlocked) return new List<EntrySummary>();
        if (string.IsNullOrWhiteSpace(keyword)) return new List<EntrySummary>();

        var kw = keyword.Trim();
        return S.Metas.Values
            .Where(m => m.Account.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        m.Note.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Id)
            .Select(m => new EntrySummary
            {
                Id = m.Id,
                CategoryIds = m.CatIds.ToList(),
                Account = m.Account,
                Note = m.Note
            }).ToList();
    }

    // 已排序的条目按 pin 位置升序在前，未参与排序的（如新增、导入）按默认顺序追加在末尾。
    // 旧实现按「位置槽位」逐一回填，多次移动后 pin 与默认顺序会互相穿插，排序结果回跳。
    private List<long> BuildAllWithPins()
    {
        var defaults = DefaultAllOrder();
        var validIds = new HashSet<long>(defaults);

        var pinned = S.AllOrderPins
            .Where(kv => validIds.Contains(kv.Key))
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        var pinnedSet = new HashSet<long>(pinned);
        var rest = defaults.Where(id => !pinnedSet.Contains(id));

        return pinned.Concat(rest).ToList();
    }

    private List<long> DefaultAllOrder()
    {
        var result = new List<long>();
        var seen = new HashSet<long>();

        foreach (var cid in S.CatOrder)
        {
            if (!S.CatIndex.TryGetValue(cid, out var entries)) continue;
            foreach (var eid in entries)
            {
                if (seen.Add(eid))
                    result.Add(eid);
            }
        }
        return result;
    }
}