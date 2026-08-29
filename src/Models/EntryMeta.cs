namespace KeySecBox;

public class EntryMeta
{
    public long Id { get; set; }
    public List<long> CatIds { get; set; } = new();
    public string Account { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}