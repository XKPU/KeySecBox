using System;
using System.Collections.Generic;

namespace KeySecBox;

/// <summary>
/// CSV/JSON 导入导出的单条记录（瞬时中间结果，不落盘）。
/// Category 仅导出侧使用；Recovery 为导入写回与导出展示的恢复密钥集合。
/// </summary>
internal readonly record struct ImportedRow(
    string Account,
    string Password,
    string Note,
    string Category = "",
    IReadOnlyList<string>? Recovery = null);

/// <summary>多值字段（分类名、恢复密钥）在 CSV 中的拼接规则：分隔符 &n，前后各一个空格。</summary>
internal static class MultiValueFormat
{
    public const string Separator = " &n ";

    /// <summary>按 &n 拆分多值（兼容缺失空格的写法），去除空白与重复项。</summary>
    public static List<string> Split(string value)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return parts;

        // 不含分隔符时整体作为一个值，避免误伤含 '&' 的名称（如 "R&D"）
        if (!value.Contains("&n", StringComparison.OrdinalIgnoreCase))
        {
            var single = value.Trim();
            if (single.Length > 0) parts.Add(single);
            return parts;
        }

        int i = 0;
        while (i < value.Length)
        {
            int idx = value.IndexOf("&n", i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { Add(value[i..]); break; }
            Add(value[i..idx]);
            i = idx + 2;
        }
        return parts;

        void Add(string s)
        {
            var t = s.Trim();
            if (t.Length > 0 && !parts.Contains(t)) parts.Add(t);
        }
    }
}

/// <summary>导出结果统计。</summary>
internal readonly record struct CsvExportResult(int Exported);

/// <summary>
/// 字段级变换插件协议：导入侧做字段变换，导出侧做字段格式化。
/// 新增来源/新格式只需增加实现并挂到配置上，无需改动导入导出主流程。
/// </summary>
internal interface IFieldTransform
{
    string Apply(string value);
}

/// <summary>
/// 导出格式化：多行文本在换行处压平为转义换行符「 \n 」（前后各一个空格），
/// 使一条记录始终只占 CSV 一行。
/// </summary>
internal sealed class FlattenNewlineTransform : IFieldTransform
{
    public static readonly FlattenNewlineTransform Instance = new();
    public string Apply(string value)
        => string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " \\n ");
}

/// <summary>导出列定义：表头 + 取值委托 + 可选格式化器。</summary>
internal sealed class CsvExportColumn
{
    // 导出表头使用英文键名，与其它密码管理器导出的 CSV 对齐
    public static readonly IReadOnlyList<CsvExportColumn> DefaultColumns = new[]
    {
        new CsvExportColumn { Header = ExportNames.Category, Selector = r => r.Category },
        new CsvExportColumn { Header = ExportNames.Account, Selector = r => r.Account },
        new CsvExportColumn { Header = ExportNames.Password, Selector = r => r.Password },
        new CsvExportColumn { Header = ExportNames.Note, Selector = r => r.Note, Formatter = FlattenNewlineTransform.Instance },
        new CsvExportColumn { Header = ExportNames.Recovery, Selector = r => JoinMulti(r.Recovery), Formatter = FlattenNewlineTransform.Instance }
    };

    /// <summary>多值字段（分类、恢复密钥）的拼接分隔符，前后各一个空格。</summary>
    public static string JoinMulti(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return "";
        var parts = new List<string>(values.Count);
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var t = v.Trim();
            if (!parts.Contains(t)) parts.Add(t);
        }
        return string.Join(MultiValueFormat.Separator, parts);
    }

    public string Header { get; init; } = "";
    public Func<ImportedRow, string> Selector { get; init; } = _ => "";

    /// <summary>可选：写列前对取值做格式化（日期、多值拼接等）。</summary>
    public IFieldTransform? Formatter { get; init; }
}

/// <summary>导出选项：可配置的导出列集合（缺省账户/密码/备注）。</summary>
internal sealed class CsvExportOptions
{
    public IReadOnlyList<CsvExportColumn> Columns { get; init; } = CsvExportColumn.DefaultColumns;

    public static readonly CsvExportOptions Default = new();
}
