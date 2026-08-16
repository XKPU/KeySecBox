using System.Text;

namespace KeySecBox;

/// <summary>极简 RFC4180 CSV 读写（仅覆盖应用内所需列语义）。</summary>
internal static class Csv
{
    /// <summary>解析 CSV 文本为行/列。支持带引号字段与内含逗号/换行/双引号。</summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool cellStart = true;

        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ',';
            bool end = i == text.Length;

            if (end)
            {
                if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
                break;
            }

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cellStart && cell.Length == 0)
            {
                inQuotes = true;
                cellStart = false;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellStart = true;
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString());
                rows.Add(row.ToArray());
                row = new List<string>();
                cell.Clear();
                cellStart = true;
            }
            else if (c != '\r')
            {
                cell.Append(c);
                cellStart = false;
            }
        }
        return rows;
    }

    /// <summary>字段转义：含逗号/引号/换行时加引号并转义引号。</summary>
    public static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
