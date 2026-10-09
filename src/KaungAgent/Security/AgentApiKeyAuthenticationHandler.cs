using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using KaungAgent.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KaungAgent.Security;

public sealed class AgentApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentAuthOptions> agentOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var suppliedKey = Request.Headers[AgentSecurityConstants.Header].ToString();
        if (string.IsNullOrEmpty(suppliedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(agentOptions.Value.ApiKey));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
        {
            return Task.FromResult(AuthenticateResult.Fail("The KaungAgent API key is invalid."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "kaung-agent-client"), new Claim(ClaimTypes.Name, "KaungAgent API client")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
