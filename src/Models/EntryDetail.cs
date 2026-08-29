namespace KeySecBox;

public class EntryDetail
{
    public long Id { get; set; }
    public List<long> CategoryIds { get; set; } = new();
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}