namespace KeySecBox;

public class CategoryItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool CanMoveUp { get; set; }
    public bool CanMoveDown { get; set; }
    public bool ShowSortArrows { get; set; }
    public bool ShowActionButtons { get; set; }
}

public class EntryItem
{
    public long Id { get; set; }
    public string Account { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string NoteDisplay => Note.Length > 40 ? Note[..40] + "…" : Note;
    public string CategoryName { get; set; } = string.Empty;
    public List<long> CategoryIds { get; set; } = new();
    public bool CanMoveUp { get; set; }
    public bool CanMoveDown { get; set; }
}
