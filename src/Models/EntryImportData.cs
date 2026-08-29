namespace KeySecBox;

public class EntryImportData
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = new();
}