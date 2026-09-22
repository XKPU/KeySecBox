namespace KeySecBox;

/// <summary>导出方式。条目类导出依赖条目数据，目录类导出依赖 data 目录内容。</summary>
internal enum ExportMethod
{
    /// <summary>明文 CSV。</summary>
    Csv,
    /// <summary>加密 CSV（默认沿用保险库密码，可自定义）。</summary>
    EncryptedCsv,
    /// <summary>完整 data 目录（明文拷贝，不含日志）。</summary>
    DataDirectory,
    /// <summary>完整 data 目录打包为单个加密文件。</summary>
    EncryptedDataDirectory
}

/// <summary>导出文件的字段名/键名（一律英文，便于与其它密码管理器交换）。</summary>
internal static class ExportNames
{
    public const string Category = "category";
    public const string Account = "account";
    public const string Password = "password";
    public const string Note = "note";
    public const string Recovery = "recovery";
}

/// <summary>一次导出请求（经密码二次确认后产生，含加密密码）。</summary>
internal sealed class ExportRequest
{
    public ExportRequest(ExportMethod method, string? encryptionPassword)
    {
        Method = method;
        EncryptionPassword = encryptionPassword;
    }

    public ExportMethod Method { get; }

    /// <summary>CSV 类导出时携带的分类筛选与字段名偏好；目录类导出为 null。</summary>
    public CsvExportPreferences? CsvPrefs { get; set; }

    /// <summary>加密用密码；明文导出为 null。</summary>
    public string? EncryptionPassword { get; private set; }

    public bool IsEncrypted => Method is ExportMethod.EncryptedCsv
        or ExportMethod.EncryptedDataDirectory;

    public bool IsDirectoryExport => Method is ExportMethod.DataDirectory
        or ExportMethod.EncryptedDataDirectory;

    /// <summary>导出完成后丢弃明文密码引用。</summary>
    public void Clear() => EncryptionPassword = null;
}

/// <summary>
/// CSV 导出偏好：分类筛选、是否保留分类列、字段名自定义。
/// 仅 CSV 类导出使用；目录类导出忽略。
/// </summary>
internal sealed class CsvExportPreferences
{
    /// <summary>null = 导出全部分类；否则仅导出命中（含未分类 UncatId）的分类。</summary>
    public IReadOnlySet<long>? CategoryIds { get; init; }

    /// <summary>是否在导出文件中保留分类列（默认 true）。</summary>
    public bool IncludeCategory { get; init; } = true;

    /// <summary>字段表头：[分类, 账户, 密码, 备注, 恢复密钥]，默认英文。</summary>
    public IReadOnlyList<string> HeaderNames { get; init; } = new[]
    {
        ExportNames.Category, ExportNames.Account, ExportNames.Password, ExportNames.Note, ExportNames.Recovery
    };
}

/// <summary>导出结果。</summary>
internal sealed class ExportResult
{
    /// <summary>条目类导出为记录数；目录类导出为文件数。</summary>
    public int Count { get; init; }

    public string Target { get; init; } = "";
}
