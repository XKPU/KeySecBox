using System.Text;

namespace KeySecBox;

public interface ICsvService
{
    List<EntryImportData> ImportFromCsv(string filePath);
    ErrorCodes ExportToCsv(string filePath, IEnumerable<EntryImportData> entries);
}

public class CsvService : ICsvService
{
    public List<EntryImportData> ImportFromCsv(string filePath)
    {
        var result = new List<EntryImportData>();
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        if (lines.Length < 2) return result;

        var header = lines[0].Split(',');
        int idxAccount = Array.FindIndex(header, h => h.Trim().Equals("Account", StringComparison.OrdinalIgnoreCase));
        int idxPassword = Array.FindIndex(header, h => h.Trim().Equals("Password", StringComparison.OrdinalIgnoreCase));
        int idxNote = Array.FindIndex(header, h => h.Trim().Equals("Note", StringComparison.OrdinalIgnoreCase));
        int idxCategories = Array.FindIndex(header, h => h.Trim().Equals("Categories", StringComparison.OrdinalIgnoreCase));

        if (idxAccount < 0) return result;

        for (int i = 1; i < lines.Length; i++)
        {
            var fields = ParseCsvLine(lines[i]);
            var entry = new EntryImportData
            {
                Account = idxAccount < fields.Length ? Unquote(fields[idxAccount]) : "",
                Password = idxPassword >= 0 && idxPassword < fields.Length ? Unquote(fields[idxPassword]) : "",
                Note = idxNote >= 0 && idxNote < fields.Length ? Unquote(fields[idxNote]) : ""
            };

            if (idxCategories >= 0 && idxCategories < fields.Length)
            {
                var catStr = Unquote(fields[idxCategories]);
                entry.Categories = catStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .Where(c => c.Length > 0)
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(entry.Account))
                result.Add(entry);
        }

        return result;
    }

    public ErrorCodes ExportToCsv(string filePath, IEnumerable<EntryImportData> entries)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Account,Password,Note,Categories");
            foreach (var e in entries)
            {
                sb.Append(Escape(e.Account)).Append(',');
                sb.Append(Escape(e.Password)).Append(',');
                sb.Append(Escape(e.Note)).Append(',');
                sb.AppendLine(Escape(string.Join(";", e.Categories)));
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            return ErrorCodes.Ok;
        }
        catch { return ErrorCodes.IO; }
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private static string Unquote(string s)
    {
        return s.Trim();
    }

    private static string Escape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }
}