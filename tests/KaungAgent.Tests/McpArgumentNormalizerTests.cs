using System.Text.Json;
using KaungAgent.Mcp;

namespace KaungAgent.Tests;

public sealed class McpArgumentNormalizerTests
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            store_ref = new { type = "object" },
            store_refs = new { type = "array" },
            member_ref = new { type = "object" },
            partition_years = new { type = "array" },
            name_keyword = new { type = "string" }
        }
    });

    [Fact]
    public void Stringified_objects_and_arrays_are_normalized_to_json_values()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["store_ref"] = JsonSerializer.SerializeToElement("{\"code_type\":\"store_code\",\"code\":\"001234\"}"),
            ["partition_years"] = "[\"2025\",\"2026\"]",
            ["name_keyword"] = "kept as text"
        };

        var normalized = McpArgumentNormalizer.Normalize(Schema, arguments);

        var storeRef = Assert.IsType<JsonElement>(normalized["store_ref"]);
        Assert.Equal(JsonValueKind.Object, storeRef.ValueKind);
        Assert.Equal("001234", storeRef.GetProperty("code").GetString());
        var partitionYears = Assert.IsType<JsonElement>(normalized["partition_years"]);
        Assert.Equal(JsonValueKind.Array, partitionYears.ValueKind);
        Assert.Equal(2, partitionYears.GetArrayLength());
        Assert.Equal("kept as text", normalized["name_keyword"]);
    }

    [Fact]
    public void Numeric_and_plain_text_store_codes_use_the_store_code_namespace()
    {
        var numeric = McpArgumentNormalizer.Normalize(
            Schema,
            new Dictionary<string, object?> { ["store_ref"] = JsonSerializer.SerializeToElement(2002) });
        var text = McpArgumentNormalizer.Normalize(
            Schema,
            new Dictionary<string, object?> { ["store_ref"] = "002002" });

        var numericRef = Assert.IsType<JsonElement>(numeric["store_ref"]);
        Assert.Equal("store_code", numericRef.GetProperty("code_type").GetString());
        Assert.Equal("2002", numericRef.GetProperty("code").GetString());
        var textRef = Assert.IsType<JsonElement>(text["store_ref"]);
        Assert.Equal("store_code", textRef.GetProperty("code_type").GetString());
        Assert.Equal("002002", textRef.GetProperty("code").GetString());
    }

    [Fact]
    public void Store_reference_arrays_accept_scalar_codes()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["store_refs"] = JsonSerializer.SerializeToElement(new object[] { 2002, "002003" })
        };

        var normalized = McpArgumentNormalizer.Normalize(Schema, arguments);

        var storeRefs = Assert.IsType<JsonElement>(normalized["store_refs"]);
        Assert.Equal(JsonValueKind.Array, storeRefs.ValueKind);
        Assert.All(storeRefs.EnumerateArray(), item => Assert.Equal("store_code", item.GetProperty("code_type").GetString()));
        Assert.Equal("2002", storeRefs[0].GetProperty("code").GetString());
        Assert.Equal("002003", storeRefs[1].GetProperty("code").GetString());
    }

    [Fact]
    public void Unknown_object_shorthands_fail_before_the_remote_tool_is_called()
    {
        var arguments = new Dictionary<string, object?> { ["member_ref"] = "not-json" };

        var exception = Assert.Throws<ArgumentException>(() => McpArgumentNormalizer.Normalize(Schema, arguments));

        Assert.Contains("member_ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains("JSON 对象", exception.Message, StringComparison.Ordinal);
    }
}
