namespace KaungMcp.Security;

public sealed record ValidatedClient(Guid Id, string Name);

public interface IApiKeyValidator
{
    Task<ValidatedClient?> ValidateAsync(string apiKey, CancellationToken cancellationToken);
}
