using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace KaungAgent.Security;

public sealed class ProtectedSecretStore(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("KaungAgent.Secrets.v1");

    public string? Protect(string? value) => string.IsNullOrEmpty(value) ? null : _protector.Protect(value);

    public string? Unprotect(string? value) => string.IsNullOrEmpty(value) ? null : _protector.Unprotect(value);

    public string? ProtectHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers is null || headers.Count == 0 ? null : Protect(JsonSerializer.Serialize(headers));

    public IReadOnlyDictionary<string, string> UnprotectHeaders(string? protectedJson)
    {
        var json = Unprotect(protectedJson);
        return string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }
}
