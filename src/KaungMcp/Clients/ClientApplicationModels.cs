namespace KaungMcp.Clients;

public sealed record CreateClientRequest(string Name, string? Description);

public sealed record CreatedClientResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string ApiKey,
    DateTimeOffset CreatedAtUtc);

public sealed record ClientResponse(
    Guid Id,
    string Name,
    string? Description,
    string KeyId,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed record RotatedClientKeyResponse(Guid Id, string ApiKey, DateTimeOffset RotatedAtUtc);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
