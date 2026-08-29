namespace KeySecBox;

public partial class VaultService
{
    // 返回新分类 Id（正数）；失败返回 -(long)ErrorCodes.X（负数）。
    // 新分类 Id 从 1 递增，与错误码 1..7 区间重叠，故错误必须取负，
    // 否则调用方无法区分「新建成功、Id 为 6」与「重名失败、Dup=6」。
    public long AddCategory(string name)
    {
        if (!S.Unlocked) return -(long)ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return -(long)ErrorCodes.Generic;
        if (string.IsNullOrWhiteSpace(name)) return -(long)ErrorCodes.Generic;

        name = name.Trim();
        if (name == VaultStore.UncatName) return -(long)ErrorCodes.Dup;
        if (S.Categories.Values.Any(c => c.Name == name)) return -(long)ErrorCodes.Dup;

        long id = S.NextCatId++;
        S.Categories[id] = new Category { Id = id, Name = name };
        S.CatIndex[id] = new List<long>();
        S.CatOrder.Add(id);
        S.MetaDirty = true;
        Diag("add_category: id={0} name={1}", id, name);
        return id;
    }

    public ErrorCodes RenameCategory(long id, string name)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (id == VaultStore.UncatId) return ErrorCodes.Generic;
        if (!S.Categories.ContainsKey(id)) return ErrorCodes.NotFound;
        if (string.IsNullOrWhiteSpace(name)) return ErrorCodes.Generic;

        name = name.Trim();
        if (S.Categories.Values.Any(c => c.Id != id && c.Name == name)) return ErrorCodes.Dup;

        S.Categories[id].Name = name;
        S.MetaDirty = true;
        Diag("rename_category: id={0} name={1}", id, name);
        return ErrorCodes.Ok;
    }

    public ErrorCodes MoveCategory(long id, long newPos)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (id == VaultStore.UncatId) return ErrorCodes.Generic;
        if (!S.Categories.ContainsKey(id)) return ErrorCodes.NotFound;

        S.CatOrder.Remove(id);
        newPos = Math.Clamp(newPos, 1, S.CatOrder.Count);
        S.CatOrder.Insert((int)newPos, id);
        S.MetaDirty = true;
        Diag("move_category: id={0} pos={1}", id, newPos);
        return ErrorCodes.Ok;
    }

    public ErrorCodes RemoveCategory(long id)
    {
        if (!S.Unlocked) return ErrorCodes.NotUnlocked;
        if (S.LegacyMode) return ErrorCodes.Generic;
        if (id == VaultStore.UncatId) return ErrorCodes.Generic;
        if (!S.Categories.ContainsKey(id)) return ErrorCodes.NotFound;

        var entriesToRemove = new List<long>();
        if (S.CatIndex.TryGetValue(id, out var catEntries))
        {
            foreach (var eid in catEntries.ToList())
            {
                if (S.Metas.TryGetValue(eid, out var meta))
                {
                    meta.CatIds.Remove(id);
                    if (meta.CatIds.Count == 0)
                        entriesToRemove.Add(eid);
                }
            }
        }

        foreach (var eid in entriesToRemove)
            RemoveEntry(eid);

        S.Categories.Remove(id);
        S.CatIndex.Remove(id);
        S.CatOrder.Remove(id);
        S.MetaDirty = true;
        Diag("remove_category: id={0}", id);
        return ErrorCodes.Ok;
    }

    public List<Category> ListCategories()
    {
        if (!S.Unlocked) return new List<Category>();
        return S.CatOrder
            .Where(id => S.Categories.ContainsKey(id))
            .Select(id => S.Categories[id])
            .ToList();
    }
}