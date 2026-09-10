using System;
using System.Collections.Generic;
using System.IO;

namespace KeySecBox;

/// <summary>
/// 导出服务：按选定方式输出明文/加密的 CSV，或完整 data 目录（明文拷贝 / 加密打包）。
/// 条目类导出全程流式，加密分支先在内存中组装再整体封装（AES-GCM 需完整明文）。
/// </summary>
internal sealed class ExportService
{
    private readonly NativeMethods.Store _store;

    public ExportService(NativeMethods.Store store) => _store = store;

    public ExportResult Execute(ExportRequest request, string target)
        => request.Method switch
        {
            ExportMethod.Csv or ExportMethod.EncryptedCsv => ExportRecords(request, target),
            ExportMethod.DataDirectory => CopyDataDirectory(target),
            ExportMethod.EncryptedDataDirectory => ExportEncryptedDirectory(request, target),
            _ => throw new NotSupportedException($"不支持的导出方式：{request.Method}")
        };

    #region 条目导出（CSV，明文或加密）

    private ExportResult ExportRecords(ExportRequest request, string target)
    {
        var prefs = request.CsvPrefs ?? new CsvExportPreferences();
        var columns = BuildColumns(prefs);
        int count = 0;

        if (request.IsEncrypted)
        {
            using var buffer = new MemoryStream();
            WriteCsv(buffer, columns, Rows(prefs, () => count++));
            BackupCrypto.EncryptToFile(target, buffer.ToArray(), request.EncryptionPassword!);
        }
        else
        {
            using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteCsv(file, columns, Rows(prefs, () => count++));
        }

        return new ExportResult { Count = count, Target = target };
    }

    // 逐条解密取密码（GetEntry 是唯一返回密码的入口），取到即断开引用
    private IEnumerable<ImportedRow> Rows(CsvExportPreferences prefs, Action onRow)
    {
        var categoryNames = new Dictionary<long, string>();
        foreach (var cat in _store.ListCategories()) categoryNames[cat.Id] = cat.Name;

        var filter = prefs.CategoryIds;
        foreach (var ent in _store.QueryAll())
        {
            var full = _store.GetEntry(ent.Id);
            if (full == null) continue;

            // 分类筛选：未选中任何分类即全选；否则命中其一才导出
            if (filter != null && !MatchesFilter(full, filter))
            {
                DropRefs(full);
                continue;
            }

            onRow();
            // 恢复密钥独立存储，需按条目 id 单独读取
            yield return BuildRow(full, categoryNames, prefs.IncludeCategory, _store.GetRecovery(ent.Id));
        }
    }

    private static bool MatchesFilter(NativeMethods.Entry full, IReadOnlySet<long> filter)
    {
        var ids = full.CategoryIds;
        if (ids is { Count: > 0 })
        {
            foreach (var id in ids) if (filter.Contains(id)) return true;
            return false; // 有分类但均不在筛选集内
        }
        return filter.Contains(NativeMethods.UncatId); // 无分类归入未分类
    }

    private static void DropRefs(NativeMethods.Entry full)
        => full.Password = full.Note = full.Account = "";

    private static ImportedRow BuildRow(NativeMethods.Entry full, Dictionary<long, string> categoryNames,
        bool includeCategory, List<string>? recovery)
    {
        // 无分类归入未分类；多分类按分隔符拼接，重复名与已删除分类自动剔除
        var ids = full.CategoryIds is { Count: > 0 } own ? own : new List<long> { NativeMethods.UncatId };
        var names = new List<string>();
        foreach (var id in ids)
        {
            if (categoryNames.TryGetValue(id, out var n) && n.Length > 0 && !names.Contains(n))
                names.Add(n);
        }

        // 不保留分类列时置空，避免冗余数据
        var category = includeCategory
            ? (names.Count > 0 ? string.Join(MultiValueFormat.Separator, names) : "未分类")
            : "";

        var keys = new List<string>();
        if (recovery is { Count: > 0 })
        {
            foreach (var k in recovery)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var t = k.Trim();
                if (!keys.Contains(t)) keys.Add(t);
            }
        }

        var row = new ImportedRow(full.Account ?? "", full.Password ?? "", full.Note ?? "", category, keys);
        full.Password = ""; full.Note = ""; full.Account = ""; // 立即断开引用
        return row;
    }

    // 按偏好构造导出列：可去掉分类列，表头可用自定义名
    private static IReadOnlyList<CsvExportColumn> BuildColumns(CsvExportPreferences prefs)
    {
        var names = prefs.HeaderNames;
        // 索引定值：0 分类、1 账户、2 密码、3 备注、4 恢复密钥；缺失或空白回退默认英文
        string H(int i, string fallback)
            => i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i].Trim() : fallback;

        var cols = new List<CsvExportColumn>(5);
        if (prefs.IncludeCategory)
            cols.Add(new CsvExportColumn { Header = H(0, ExportNames.Category), Selector = r => r.Category });
        cols.Add(new CsvExportColumn { Header = H(1, ExportNames.Account), Selector = r => r.Account });
        cols.Add(new CsvExportColumn { Header = H(2, ExportNames.Password), Selector = r => r.Password });
        cols.Add(new CsvExportColumn { Header = H(3, ExportNames.Note), Selector = r => r.Note, Formatter = FlattenNewlineTransform.Instance });
        cols.Add(new CsvExportColumn { Header = H(4, ExportNames.Recovery), Selector = r => CsvExportColumn.JoinMulti(r.Recovery), Formatter = FlattenNewlineTransform.Instance });
        return cols;
    }

    // 备注多行由 FlattenNewlineTransform 压平，保证一条记录只占一行
    private static void WriteCsv(Stream stream, IReadOnlyList<CsvExportColumn> columns, IEnumerable<ImportedRow> rows)
        => new CsvExportService().Export(stream, rows, new CsvExportOptions { Columns = columns });

    #endregion

    #region 数据目录导出

    /// <summary>把 data 目录（不含日志）完整拷贝到 <paramref name="targetDir"/>。</summary>
    private static ExportResult CopyDataDirectory(string targetDir)
    {
        int files = 0;
        foreach (var rel in EnumerateDataFiles())
        {
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(AppPaths.DataDir, rel), dest, overwrite: true);
            files++;
        }
        return new ExportResult { Count = files, Target = targetDir };
    }

    /// <summary>把 data 目录（不含日志）打包为单个加密文件。</summary>
    private static ExportResult ExportEncryptedDirectory(ExportRequest request, string target)
    {
        var files = new List<string>(EnumerateDataFiles());
        var payload = BackupArchive.Pack(AppPaths.DataDir, files);
        BackupCrypto.EncryptToFile(target, payload, request.EncryptionPassword!);
        return new ExportResult { Count = files.Count, Target = target };
    }

    /// <summary>data 目录下需导出的文件（相对路径），排除日志文件。</summary>
    private static IEnumerable<string> EnumerateDataFiles()
    {
        if (!Directory.Exists(AppPaths.DataDir)) yield break;

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(AppPaths.TraceLog),
            Path.GetFileName(AppPaths.CrashLog)
        };

        foreach (var file in Directory.EnumerateFiles(AppPaths.DataDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (skip.Contains(name) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Path.GetRelativePath(AppPaths.DataDir, file);
        }
    }

    #endregion
}
