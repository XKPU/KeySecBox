using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace KeySecBox;

/// <summary>
/// 基于 CsvHelper 的流式 CSV 导出服务：按可配置列集合逐条写出 UTF-8 CSV，
/// 字段含逗号/引号/换行由引擎按 RFC 4180 自动转义；不整表缓存明文。
/// </summary>
internal sealed class CsvExportService
{
    /// <summary>流式导出到文件 <paramref name="filePath"/>；返回写出的记录数。</summary>
    /// <exception cref="IOException">文件不可写。</exception>
    public CsvExportResult Export(string filePath, IEnumerable<ImportedRow> rows, CsvExportOptions? options)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        return Export(stream, rows, options);
    }

    /// <summary>流式导出到 <paramref name="stream"/>（不关闭传入流）；返回写出的记录数。</summary>
    public CsvExportResult Export(Stream stream, IEnumerable<ImportedRow> rows, CsvExportOptions? options)
    {
        var columns = options?.Columns is { Count: > 0 } cols ? cols : CsvExportColumn.DefaultColumns;

        int exported = 0;
        // leaveOpen: true —— 加密导出复用的是 MemoryStream，写出后仍需读取
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        using var csv = new CsvWriter(writer, CreateConfiguration());

        foreach (var col in columns)
            csv.WriteField(col.Header);
        csv.NextRecord();

        foreach (var row in rows)
        {
            foreach (var col in columns)
            {
                var value = col.Selector?.Invoke(row) ?? "";
                if (col.Formatter is { } fmt) value = fmt.Apply(value);
                csv.WriteField(value);
            }
            csv.NextRecord();
            exported++;
        }

        return new CsvExportResult(exported);
    }

    private static CsvConfiguration CreateConfiguration() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = false // 表头由导出列集合自行写出
    };
}
