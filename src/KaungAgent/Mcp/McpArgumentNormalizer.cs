using System.Text.Json;

namespace KaungAgent.Mcp;

internal static class McpArgumentNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, object?> Normalize(
        JsonElement inputSchema,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var normalized = arguments.ToDictionary(item => item.Key, item => item.Value);
        if (!inputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return normalized;
        }

        foreach (var argument in arguments)
        {
            if (!properties.TryGetProperty(argument.Key, out var propertySchema) ||
                !propertySchema.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var expectedType = typeElement.GetString();
            if (expectedType is not ("object" or "array"))
            {
                continue;
            }

            normalized[argument.Key] = NormalizeComplexValue(argument.Key, expectedType, argument.Value);
        }

        return normalized;
    }

    private static JsonElement NormalizeComplexValue(string name, string expectedType, object? value)
    {
        var element = value is JsonElement jsonElement
            ? jsonElement.Clone()
            : JsonSerializer.SerializeToElement(value, JsonOptions);

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    element = document.RootElement.Clone();
                }
                catch (JsonException exception)
                {
                    if (TryNormalizeStoreReference(name, element, out var storeReference))
                    {
                        return storeReference;
                    }

                    throw new ArgumentException($"参数 {name} 必须是 JSON {TypeName(expectedType)}，不能是普通字符串。", exception);
                }
            }
        }

        if (TryNormalizeStoreReference(name, element, out var normalizedStoreReference))
        {
            return normalizedStoreReference;
        }

        if (name == "store_refs" && expectedType == "array")
        {
            return NormalizeStoreReferences(element);
        }

        var expectedKind = expectedType == "object" ? JsonValueKind.Object : JsonValueKind.Array;
        if (element.ValueKind != expectedKind)
        {
            throw new ArgumentException(
                $"参数 {name} 必须是 JSON {TypeName(expectedType)}，当前类型为 {element.ValueKind}。");
        }

        return element;
    }

    private static bool TryNormalizeStoreReference(string name, JsonElement element, out JsonElement normalized)
    {
        normalized = default;
        if (name != "store_ref" || !TryGetReferenceCode(element, out var code))
        {
            return false;
        }

        normalized = CreateStoreReference(code);
        return true;
    }

    private static JsonElement NormalizeStoreReferences(JsonElement element)
    {
        var source = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [element];
        var normalized = new List<JsonElement>(source.Length);

        foreach (var item in source)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                normalized.Add(item);
                continue;
            }

            if (!TryGetReferenceCode(item, out var code))
            {
                throw new ArgumentException("参数 store_refs 的每一项必须是门店引用对象、字符串或数字编码。");
            }

            normalized.Add(CreateStoreReference(code));
        }

        return JsonSerializer.SerializeToElement(normalized, JsonOptions);
    }

    private static bool TryGetReferenceCode(JsonElement element, out string code)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                code = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(code);
            case JsonValueKind.Number:
                code = element.GetRawText();
                return true;
            default:
                code = string.Empty;
                return false;
        }
    }

    private static JsonElement CreateStoreReference(string code) => JsonSerializer.SerializeToElement(new
    {
        code_type = "store_code",
        code
    }, JsonOptions);

    private static string TypeName(string type) => type == "object" ? "对象" : "数组";
}
