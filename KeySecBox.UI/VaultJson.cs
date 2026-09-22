using Newtonsoft.Json;

namespace KeySecBox;

/// <summary>
/// 统一 JSON 序列化/反序列化封装：全工程唯一的 Newtonsoft.Json 入口。
/// 区分两类设置：与核心库交互（大小写不敏感）与配置/恢复记录落盘（属性名原样）。
/// </summary>
internal static class VaultJson
{
    // 核心库交互：C++ 侧返回小写 camelCase JSON，需大小写不敏感匹配。
    // Entry/Category 仅做反序列化、从不回传（回传只 List<long>/List<string> 数组），无需命名转换 resolver。
    // C++ 同时输出 categoryId 与 categoryIds：必须整表替换集合，否则 Newtonsoft 默认
    // 追加进既有 List（System.Text.Json 是替换），同一个分类会被显示两次。
    private static readonly JsonSerializerSettings _coreApiSettings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    // 配置/恢复记录落盘：属性名已是 camelCase，原样读写，不做大小写转换。
    private static readonly JsonSerializerSettings _persistSettings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    public static JsonSerializerSettings CreateCoreApiSettings() => _coreApiSettings;

    public static JsonSerializerSettings CreatePersistSettings() => _persistSettings;

    /// <summary>容错反序列化：空串/损坏 JSON 返回 default，不抛未捕获异常。</summary>
    public static T? DeserializeOrDefault<T>(string? json, JsonSerializerSettings settings)
    {
        if (string.IsNullOrEmpty(json)) return default;
        try
        {
            return JsonConvert.DeserializeObject<T>(json, settings);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>序列化为合规 JSON 文本。</summary>
    public static string Serialize<T>(T value, JsonSerializerSettings settings)
        => JsonConvert.SerializeObject(value, settings);

    /// <summary>注册自定义类型转换器（供高级导入导出自定义转换扩展）。</summary>
    public static void RegisterGlobalConverter(JsonConverter converter)
    {
        _coreApiSettings.Converters.Add(converter);
        _persistSettings.Converters.Add(converter);
    }
}