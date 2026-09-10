using System;
using System.Collections.Generic;
using System.Linq;

namespace KeySecBox;

/// <summary>
/// 字段键名表达式：规范写法以 ${} 包裹，内部支持多层路径与通配符。
/// <para>
/// 语法：${段1/段2/…}，段间以 / 分隔；每段可为：
/// 键名（对象属性）、数组索引（数字，支持负数表示倒数）、通配符 *（展开当前层所有键或元素）。
/// 例：${1/:*/:2} —— 取第 1 项，展开其下所有子项，各取索引 2（可产生多个值）。
/// </para>
/// 兼容：段前允许带 ':'（故 ":*" 等同 "*"）；未用 ${} 包裹时按整串路径解析。
/// </summary>
internal sealed class KeyExpression
{
    private KeyExpression(string raw, IReadOnlyList<string> segments)
    {
        Raw = raw;
        Segments = segments;
    }

    /// <summary>原始输入文本。</summary>
    public string Raw { get; }

    /// <summary>归一化后的路径段。</summary>
    public IReadOnlyList<string> Segments { get; }

    public bool IsEmpty => Segments.Count == 0;

    /// <summary>扁平数据（CSV）使用的键名：单段即该段，多段时取最后一段。</summary>
    public string FlatKey => Segments.Count == 0 ? "" : Segments[Segments.Count - 1];

    public static KeyExpression Parse(string? input)
    {
        var text = (input ?? "").Trim();
        var inner = text;

        if (text.StartsWith("${", StringComparison.Ordinal)
            && text.EndsWith("}", StringComparison.Ordinal)
            && text.Length >= 3)
        {
            inner = text.Substring(2, text.Length - 3);
        }

        var segments = new List<string>();
        foreach (var raw in inner.Split('/'))
        {
            var seg = raw.Trim();
            if (seg.StartsWith(":", StringComparison.Ordinal)) seg = seg.Substring(1).Trim(); // 兼容 ":*" / ":2"
            if (seg.Length > 0) segments.Add(seg);
        }

        return new KeyExpression(text, segments);
    }

    /// <summary>格式化为规范写法 ${key}。</summary>
    public static string Format(string key) => "${" + key + "}";

    /// <summary>按行解析多个表达式（备注/恢复密钥允许多个键名）。</summary>
    public static List<KeyExpression> ParseLines(string text)
        => (text ?? "")
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Parse)
            .Where(e => !e.IsEmpty)
            .GroupBy(e => e.Raw, StringComparer.OrdinalIgnoreCase) // 去重
            .Select(g => g.First())
            .ToList();

    public override string ToString() => Raw;
}
