using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KeySecBox;

/// <summary>
/// 高级导入的数据源：把 CSV / JSON 解析为可按键名表达式取值的记录集合。
/// CSV 为扁平「列名 → 值」；JSON 保留原始对象，以支持多层路径与通配符。
/// 支持本程序导出的加密 CSV（.csvenc）：先识别 magic，解密后再按明文解析。
/// </summary>
internal sealed class ImportSource
{
    public ImportSourceKind Kind { get; init; }

    /// <summary>原始文本内容（供界面右侧展示、允许复制）。</summary>
    public string RawText { get; init; } = "";

    /// <summary>解析出的键名集合（CSV 表头 / JSON 顶层属性名）。</summary>
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

    /// <summary>CSV：扁平记录。</summary>
    public IReadOnlyList<Dictionary<string, string>> Records { get; init; } = new List<Dictionary<string, string>>();

    /// <summary>JSON：原始对象，供多层路径求值。</summary>
    public IReadOnlyList<JObject> JsonRecords { get; init; } = new List<JObject>();

    public int Count => Kind == ImportSourceKind.Json ? JsonRecords.Count : Records.Count;

    /// <summary>是否为「本程序标准 CSV/JSON」：可识别账户与密码键名，可自动填字段。</summary>
    public bool IsStandardFormat { get; init; }

    #region 载入

    /// <summary>读取明文文本文件（自动识别 BOM）。</summary>
    public static string ReadText(string path)
    {
        using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>文件是否为加密导出文件（按 magic 判断，与扩展名无关）。</summary>
    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[BackupCrypto.Magic.Length];
            if (fs.Read(buf, 0, buf.Length) != buf.Length) return false;
            return Encoding.ASCII.GetString(buf) == BackupCrypto.Magic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解密导出文件；密码错误或文件损坏返回 null。</summary>
    public static string? TryDecrypt(string path, string password)
    {
        var bytes = BackupCrypto.DecryptFromFile(path, password);
        if (bytes is null) return null;

        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static ImportSource FromCsv(string text)
    {
        var headers = new List<string>();
        var records = new List<Dictionary<string, string>>();

        try
        {
            using var reader = new StringReader(text);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true, // 首行即表头，高级导入由用户指定键名
                TrimOptions = TrimOptions.Trim,
                IgnoreBlankLines = true,
                MissingFieldFound = null, // 短行不抛异常
                BadDataFound = null,
                LineBreakInQuotedFieldIsBadData = false
            });

            if (csv.Read())
            {
                csv.ReadHeader();
                foreach (var h in csv.HeaderRecord ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(h) && !headers.Contains(h)) headers.Add(h);

                while (true)
                {
                    try { if (!csv.Read()) break; }
                    catch (CsvHelperException) { continue; } // 坏行隔离

                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in headers) dict[h] = SafeGet(csv, h);
                    if (dict.Values.Any(v => v.Length > 0)) records.Add(dict);
                }
            }
        }
        catch (CsvHelperException) { }

        return new ImportSource
        {
            Kind = ImportSourceKind.Csv,
            RawText = text,
            Headers = headers,
            Records = records,
            IsStandardFormat = LooksStandard(headers)
        };
    }

    public static ImportSource FromJson(string text)
    {
        var headers = new List<string>();
        var jsonRecords = new List<JObject>();
        var flat = new List<Dictionary<string, string>>();

        try
        {
            using var jr = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            var token = JToken.ReadFrom(jr);

            foreach (var obj in EnumerateObjects(token))
            {
                jsonRecords.Add(obj);

                // 顶层属性名既作为字段提示，也保留一份扁平视图（兼容单层键名）
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties())
                {
                    dict[p.Name] = Flatten(p.Value);
                    if (!headers.Contains(p.Name)) headers.Add(p.Name);
                }
                flat.Add(dict);
            }
        }
        catch (JsonException) { }

        return new ImportSource
        {
            Kind = ImportSourceKind.Json,
            RawText = text,
            Headers = headers,
            JsonRecords = jsonRecords,
            Records = flat, // 单层键名可直接复用扁平查找
            IsStandardFormat = LooksStandard(headers)
        };
    }

    private static string SafeGet(CsvReader csv, string header)
    {
        try { return csv.GetField(header) ?? ""; }
        catch (CsvHelperException) { return ""; }
    }

    /// <summary>根为数组时逐元素；根为对象时取其首个「元素为对象」的数组属性，否则自身作为单条记录。</summary>
    private static IEnumerable<JObject> EnumerateObjects(JToken token)
    {
        if (token is JArray arr)
        {
            foreach (var item in arr)
                if (item is JObject o) yield return o;
            yield break;
        }

        if (token is JObject root)
        {
            foreach (var p in root.Properties())
            {
                if (p.Value is JArray { Count: > 0 } a && a[0] is JObject)
                {
                    foreach (var item in a)
                        if (item is JObject io) yield return io;
                    yield break;
                }
            }
            yield return root;
        }
    }

    private static string Flatten(JToken? t)
    {
        if (t is null || t.Type == JTokenType.Null) return "";
        if (t is JValue v) return v.Value?.ToString() ?? "";
        return t.ToString(Formatting.None);
    }

    #endregion

    #region 取值：多层路径与通配符

    /// <summary>
    /// 按表达式取第 index 条记录的值。路径含通配符时可能产生多个值（如 ${1/:*/:2}）。
    /// </summary>
    public List<string> Eval(int index, KeyExpression expr)
    {
        var none = new List<string>();
        if (expr.IsEmpty) return none;

        if (Kind == ImportSourceKind.Json)
        {
            if (index < 0 || index >= JsonRecords.Count) return none;
            return EvalPath(JsonRecords[index], expr.Segments);
        }

        if (index < 0 || index >= Records.Count) return none;
        var rec = Records[index];

        // CSV 为扁平结构：单段按列名取；多段时退化为按最后一段匹配
        if (rec.TryGetValue(expr.FlatKey, out var v) && v.Length > 0)
            return new List<string> { v };
        return none;
    }

    /// <summary>在 JSON 节点上按路径段求值；* 会展开为多个节点，故结果为多值。</summary>
    private static List<string> EvalPath(JToken root, IReadOnlyList<string> segments)
    {
        var current = new List<JToken> { root };

        foreach (var seg in segments)
        {
            var next = new List<JToken>();
            foreach (var t in current) Collect(t, seg, next);
            current = next;
            if (current.Count == 0) break;
        }

        return current
            .Select(Flatten)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void Collect(JToken node, string seg, List<JToken> sink)
    {
        if (seg == "*")
        {
            // 通配符：对象展开所有属性，数组展开所有元素
            if (node is JObject o)
            {
                foreach (var p in o.Properties()) sink.Add(p.Value);
            }
            else if (node is JArray a)
            {
                foreach (var item in a) sink.Add(item);
            }
            return;
        }

        if (int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
        {
            if (node is JArray arr)
            {
                int i = idx < 0 ? arr.Count + idx : idx; // 支持倒数索引
                if (i >= 0 && i < arr.Count) sink.Add(arr[i]);
                return;
            }
            if (node is JObject obj)
            {
                // 对象按属性顺序取第 idx 个
                var props = obj.Properties().ToList();
                int i = idx < 0 ? props.Count + idx : idx;
                if (i >= 0 && i < props.Count) sink.Add(props[i].Value);
            }
            return;
        }

        if (node is JObject o2 && o2.TryGetValue(seg, StringComparison.OrdinalIgnoreCase, out var v))
            sink.Add(v);
    }

    #endregion

    #region 标准格式识别与自动映射

    private static bool LooksStandard(IReadOnlyList<string> headers)
    {
        bool Has(params string[] names)
            => headers.Any(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));

        return Has(ExportNames.Account, "账户", "账号", "username")
            && Has(ExportNames.Password, "密码");
    }

    /// <summary>标准格式时给出自动映射（用户可再修改）；非标准返回 null。</summary>
    public FieldMapping? TryAutoMap()
    {
        if (!IsStandardFormat) return null;

        var category = Find(ExportNames.Category, "分类");
        var account = Find(ExportNames.Account, "账户", "账号", "username");
        var password = Find(ExportNames.Password, "密码");
        var note = Find(ExportNames.Note, "备注");
        var recovery = Find(ExportNames.Recovery, "恢复密钥", "recoverykeys");

        return new FieldMapping
        {
            CategoryKey = category is null ? null : KeyExpression.Parse(KeyExpression.Format(category)),
            AccountKey = KeyExpression.Parse(KeyExpression.Format(account ?? "")),
            PasswordKey = KeyExpression.Parse(KeyExpression.Format(password ?? "")),
            NoteKeys = note is null ? Array.Empty<KeyExpression>() : new[] { KeyExpression.Parse(KeyExpression.Format(note)) },
            RecoveryKeys = recovery is null ? Array.Empty<KeyExpression>() : new[] { KeyExpression.Parse(KeyExpression.Format(recovery)) }
        };
    }

    private string? Find(params string[] names)
        => Headers.FirstOrDefault(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));

    #endregion

    #region 执行导入

    /// <summary>按映射逐条写入保险库；返回成功/跳过计数。调用方负责 Save()。</summary>
    public static ImportRunResult Run(NativeMethods.Store store, ImportSource source, FieldMapping mapping)
    {
        int imported = 0, skipped = 0;

        // 分类名 → id 缓存，导入过程中新建的同名分类直接复用
        var catCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in store.ListCategories()) catCache[c.Name] = c.Id;

        for (int i = 0; i < source.Count; i++)
        {
            string account = First(source.Eval(i, mapping.AccountKey));
            string password = First(source.Eval(i, mapping.PasswordKey));
            if (account.Length == 0 && password.Length == 0) { skipped++; continue; }

            var catIds = ResolveCategories(store, source, i, mapping, catCache);
            string note = string.Join("\n", mapping.NoteKeys
                .SelectMany(k => source.Eval(i, k))
                .Where(s => s.Length > 0));

            // 每个取值各产生一把恢复密钥（通配符路径可一次取出多把）
            var keys = mapping.RecoveryKeys
                .SelectMany(k => source.Eval(i, k))
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            long id = store.AddEntry(catIds, account, password, note);
            if (id <= 0) { skipped++; continue; }
            if (keys.Count > 0) store.SetRecovery(id, keys);

            imported++;
        }

        return new ImportRunResult(imported, skipped);
    }

    private static string First(List<string> values) => values.Count > 0 ? values[0] : "";

    private static List<long> ResolveCategories(NativeMethods.Store store, ImportSource source, int index,
        FieldMapping mapping, Dictionary<string, long> cache)
    {
        var ids = new List<long>();

        if (mapping.CategoryKey is { IsEmpty: false })
        {
            // 键名优先：按 &n 拆分多分类，缺失的分类按需新建
            foreach (var value in source.Eval(index, mapping.CategoryKey))
            {
                foreach (var name in MultiValueFormat.Split(value))
                {
                    if (!cache.TryGetValue(name, out long id))
                    {
                        id = store.AddCategory(name);
                        if (id > 0) cache[name] = id;
                    }
                    if (id > 0 && !ids.Contains(id)) ids.Add(id);
                }
            }
        }
        else if (mapping.FixedCategoryId is { } fid)
        {
            ids.Add(fid);
        }

        if (ids.Count == 0) ids.Add(NativeMethods.UncatId); // 未指定归入内置「未分类」
        return ids;
    }

    #endregion
}
