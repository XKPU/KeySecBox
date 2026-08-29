using System.Text.Json;

namespace KeySecBox;

public class JsonSerializationService : IJsonSerializationService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string SerializeCategories(IEnumerable<Category> categories, IEnumerable<long> catOrder)
    {
        var catDict = categories.ToDictionary(c => c.Id);
        var list = catOrder
            .Where(id => catDict.ContainsKey(id))
            .Select(id => new { id, name = catDict[id].Name });
        return JsonSerializer.Serialize(list, Options);
    }

    public List<Category> DeserializeCategories(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var categories = new List<Category>();
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            categories.Add(new Category
            {
                Id = elem.GetProperty("id").GetInt64(),
                Name = elem.GetProperty("name").GetString() ?? ""
            });
        }
        return categories;
    }

    public string SerializeMap(long nextCatId, long nextEntryId,
        Dictionary<long, List<long>> catIndex, Dictionary<long, EntryMeta> entries, Dictionary<long, long> pins)
    {
        var obj = new Dictionary<string, object>
        {
            ["nextCatId"] = nextCatId,
            ["nextEntryId"] = nextEntryId,
            ["catIndex"] = catIndex.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value),
            ["entries"] = entries.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value.CatIds),
            ["pins"] = pins.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value)
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public (long nextCatId, long nextEntryId, Dictionary<long, List<long>> catIndex,
        Dictionary<long, EntryMeta> entries, Dictionary<long, long> pins) DeserializeMap(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long nextCatId = root.TryGetProperty("nextCatId", out var nci) ? nci.GetInt64() : 1;
        long nextEntryId = root.TryGetProperty("nextEntryId", out var nei) ? nei.GetInt64() : 1;
        if (nextCatId < 1) nextCatId = 1;
        if (nextEntryId < 1) nextEntryId = 1;

        var catIndex = new Dictionary<long, List<long>>();
        if (root.TryGetProperty("catIndex", out var ci) && ci.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in ci.EnumerateObject())
            {
                if (!long.TryParse(kv.Name, out var cid)) continue;
                var ids = new List<long>();
                foreach (var e in kv.Value.EnumerateArray())
                    ids.Add(e.GetInt64());
                catIndex[cid] = ids;
            }
        }

        var entries = new Dictionary<long, EntryMeta>();
        if (root.TryGetProperty("entries", out var ei) && ei.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in ei.EnumerateObject())
            {
                if (!long.TryParse(kv.Name, out var eid)) continue;
                var meta = new EntryMeta { Id = eid };
                foreach (var c in kv.Value.EnumerateArray())
                    meta.CatIds.Add(c.GetInt64());
                if (eid >= nextEntryId) nextEntryId = eid + 1;
                entries[eid] = meta;
            }
        }

        var pins = new Dictionary<long, long>();
        if (root.TryGetProperty("pins", out var pi) && pi.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in pi.EnumerateObject())
            {
                if (!long.TryParse(kv.Name, out var eid)) continue;
                pins[eid] = kv.Value.GetInt64();
            }
        }

        return (nextCatId, nextEntryId, catIndex, entries, pins);
    }

    public string SerializePrefs(bool diag)
    {
        return $"{{\"diag\":{(diag ? 1 : 0)}}}";
    }

    public bool DeserializePrefs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("diag", out var d) && d.GetInt64() != 0;
    }
}