namespace KeySecBox;

public class EntrySummary
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public List<long> CategoryIds { get; set; } = new();
    public string Account { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}