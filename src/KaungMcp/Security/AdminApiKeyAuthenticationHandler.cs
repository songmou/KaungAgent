using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using KaungMcp.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KaungMcp.Security;

public sealed class AdminApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AdminAuthOptions> adminOptions,
    ManagedSettingsStore settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var suppliedKey = Request.Headers[SecurityConstants.AdminHeader].ToString();
        if (string.IsNullOrEmpty(suppliedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!settings.ValidateAdminKey(suppliedKey, adminOptions.Value.Key))
        {
            return Task.FromResult(AuthenticateResult.Fail("The administrator API key is invalid."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "kaung-mcp-admin"), new Claim(ClaimTypes.Name, "KaungMcp administrator")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
