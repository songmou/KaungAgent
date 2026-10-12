using System.Text.Json;
using System.Text.RegularExpressions;
namespace KaungMcp.Plugins;

// Validates the subset emitted by our MCP SDK and plugin task schemas. Unknown constraints
// are rejected when registering a task, rather than silently promising full JSON Schema support.
public static class ArgumentSchema
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly", "format",
        "type", "enum", "const", "anyOf", "allOf", "oneOf", "required", "properties", "additionalProperties", "items",
        "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"
    };
    // format is an annotation; application-specific checks belong in IPluginTaskValidator.
    public static void CheckSupported(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数 Schema 必须为对象或布尔值。");
        foreach (var field in schema.EnumerateObject())
        {
            if (!Keywords.Contains(field.Name)) throw new ArgumentException($"不支持的参数 Schema 关键字：{field.Name}。");
            if (field.Name == "properties") foreach (var property in field.Value.EnumerateObject()) CheckSupported(property.Value);
            if (field.Name is "items" or "additionalProperties") CheckSupported(field.Value);
            if (field.Name is "anyOf" or "allOf" or "oneOf") foreach (var alternative in field.Value.EnumerateArray()) CheckSupported(alternative);
            if (field.Name == "pattern") _ = new Regex(field.Value.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }
    public static void Validate(JsonElement value, JsonElement schema, string path = "arguments")
    {
        if (schema.ValueKind is JsonValueKind.True) return;
        if (schema.ValueKind is JsonValueKind.False) throw new ArgumentException($"{path} 不接受值。");
        CheckSupported(schema);
        foreach (var keyword in new[] { "anyOf", "allOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var alternatives)) continue;
            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
                try { Validate(value, alternative, path); matches++; } catch (ArgumentException) { }
            if (keyword == "anyOf" && matches == 0 || keyword == "oneOf" && matches != 1 || keyword == "allOf" && matches != alternatives.GetArrayLength())
                throw new ArgumentException($"{path} 不符合 {keyword} 参数约束。");
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
            throw new ArgumentException($"{path} 必须使用指定值。");
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToArray() : [type.GetString()];
            if (!types.Any(t => t switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            })) throw new ArgumentException($"{path} 类型不匹配。");
        }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
            throw new ArgumentException($"{path} 不在允许的值中。");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var key in required.EnumerateArray())
                    if (!value.TryGetProperty(key.GetString()!, out _)) throw new ArgumentException($"{path}.{key.GetString()} 为必填。");
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var field in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(field.Name, out var property)) Validate(field.Value, property, path + "." + field.Name);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    if (additional.ValueKind == JsonValueKind.False) throw new ArgumentException($"{path}.{field.Name} 为未知参数。");
                    if (additional.ValueKind == JsonValueKind.Object) Validate(field.Value, additional, path + "." + field.Name);
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && length < min.GetInt32() || schema.TryGetProperty("maxItems", out var max) && length > max.GetInt32())
                throw new ArgumentException($"{path} 数量超出范围。");
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
            {
                var values = value.EnumerateArray().ToArray();
                for (var i = 0; i < values.Length; i++) for (var j = i + 1; j < values.Length; j++)
                    if (JsonElement.DeepEquals(values[i], values[j])) throw new ArgumentException($"{path} 不能包含重复项。");
            }
            if (schema.TryGetProperty("items", out var items))
                foreach (var item in value.EnumerateArray()) Validate(item, items, path + "[]");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException($"{path} 格式不匹配。");
            var length = text.EnumerateRunes().Count();
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32() || schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
                throw new ArgumentException($"{path} 长度超出范围。");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMin) && number <= exclusiveMin.GetDouble() ||
                schema.TryGetProperty("exclusiveMaximum", out var exclusiveMax) && number >= exclusiveMax.GetDouble())
                throw new ArgumentException($"{path} 数值超出范围。");
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() || schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                throw new ArgumentException($"{path} 数值超出范围。");
        }
    }
}
