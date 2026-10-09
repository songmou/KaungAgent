using KaungMcp.Security;

namespace KaungMcp.Tests;

public sealed class ApiKeyCodecTests
{
    [Fact]
    public void Generated_key_round_trips_without_storing_plain_text()
    {
        var generated = ApiKeyCodec.Generate();

        Assert.StartsWith("kmcp_", generated.PlainText, StringComparison.Ordinal);
        Assert.True(ApiKeyCodec.TryGetKeyId(generated.PlainText, out var keyId));
        Assert.Equal(generated.KeyId, keyId);
        Assert.True(ApiKeyCodec.Verify(generated.PlainText, generated.Hash));
        Assert.False(ApiKeyCodec.Verify($"{generated.PlainText}x", generated.Hash));
        Assert.Equal(32, generated.Hash.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-kaung-key")]
    [InlineData("kmcp_missing-secret")]
    public void Invalid_key_formats_are_rejected(string value)
    {
        Assert.False(ApiKeyCodec.TryGetKeyId(value, out _));
    }
}
