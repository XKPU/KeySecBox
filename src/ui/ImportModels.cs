using System;
using System.Collections.Generic;

namespace KeySecBox;

/// <summary>导入方式（设置页导入对话框的三个选项）。</summary>
internal enum ImportMethod
{
    /// <summary>导入 CSV（.csv / .csvenc）。</summary>
    Csv,
    /// <summary>导入 JSON（.json）。</summary>
    Json,
    /// <summary>导入旧版库 1.0.x（沿用原逻辑）。</summary>
    Legacy
}

internal enum ImportSourceKind
{
    Csv,
    Json
}

/// <summary>
/// 高级导入的字段映射：把源数据中的键名表达式映射到条目字段。
/// 分类既可用键名（每行取分类名），也可固定为某一现有/新建分类；两者都为空则归入未分类。
/// </summary>
internal sealed class FieldMapping
{
    /// <summary>分类键名表达式；为空表示不使用键名。</summary>
    public KeyExpression? CategoryKey { get; init; }

    /// <summary>固定分类 id（用户选择/新建）；与 CategoryKey 二选一，键名优先。</summary>
    public long? FixedCategoryId { get; init; }

    /// <summary>账户名键名表达式（必填）。</summary>
    public KeyExpression AccountKey { get; init; } = KeyExpression.Parse("");

    /// <summary>密码键名表达式（必填）。</summary>
    public KeyExpression PasswordKey { get; init; } = KeyExpression.Parse("");

    /// <summary>备注键名表达式（可选，多个按行合并）。</summary>
    public IReadOnlyList<KeyExpression> NoteKeys { get; init; } = Array.Empty<KeyExpression>();

    /// <summary>恢复密钥键名表达式（可选，每个取值各产生一把密钥）。</summary>
    public IReadOnlyList<KeyExpression> RecoveryKeys { get; init; } = Array.Empty<KeyExpression>();
}

/// <summary>导入执行结果。</summary>
internal readonly record struct ImportRunResult(int Imported, int Skipped);
