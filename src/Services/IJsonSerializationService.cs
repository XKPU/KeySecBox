namespace KeySecBox;

public interface IJsonSerializationService
{
    string SerializeCategories(IEnumerable<Category> categories, IEnumerable<long> catOrder);
    List<Category> DeserializeCategories(string json);
    string SerializeMap(long nextCatId, long nextEntryId, Dictionary<long, List<long>> catIndex, Dictionary<long, EntryMeta> entries, Dictionary<long, long> pins);
    (long nextCatId, long nextEntryId, Dictionary<long, List<long>> catIndex, Dictionary<long, EntryMeta> entries, Dictionary<long, long> pins) DeserializeMap(string json);
    string SerializePrefs(bool diag);
    bool DeserializePrefs(string json);
}