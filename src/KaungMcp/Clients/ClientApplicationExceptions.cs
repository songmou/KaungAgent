namespace KaungMcp.Clients;

public sealed class ClientApplicationNotFoundException(Guid clientId)
    : Exception($"MCP client '{clientId}' was not found.");

public sealed class ClientApplicationConflictException(string message) : Exception(message);
