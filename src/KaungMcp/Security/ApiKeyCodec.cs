using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace KaungMcp.Security;

public sealed record GeneratedApiKey(string KeyId, string PlainText, byte[] Hash);

public static class ApiKeyCodec
{
    private const string Prefix = "kmcp_";

    public static GeneratedApiKey Generate()
    {
        var keyId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(9));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var plainText = $"{Prefix}{keyId}.{secret}";
        return new GeneratedApiKey(keyId, plainText, Hash(plainText));
    }

    public static bool TryGetKeyId(string plainText, out string keyId)
    {
        keyId = string.Empty;
        if (!plainText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = plainText.IndexOf('.', Prefix.Length);
        if (separatorIndex <= Prefix.Length || separatorIndex == plainText.Length - 1)
        {
            return false;
        }

        keyId = plainText[Prefix.Length..separatorIndex];
        return keyId.Length <= 32;
    }

    public static byte[] Hash(string plainText) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(plainText));

    public static bool Verify(string plainText, ReadOnlySpan<byte> expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(plainText), expectedHash);
}
