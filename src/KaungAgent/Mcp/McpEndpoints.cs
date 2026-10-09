using System.Text.Json;
using KaungAgent.Security;

namespace KaungAgent.Mcp;

public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mcp")
            .RequireAuthorization(AgentSecurityConstants.Policy)
            .WithTags("MCP gateway");

        group.MapGet("/tools", (IMcpGatewayClient client, CancellationToken cancellationToken) =>
            client.ListToolsAsync(cancellationToken));

        group.MapPost("/tools/{name}/invoke", async (
            string name,
            InvokeToolRequest request,
            IMcpGatewayClient client,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Tool name is required.", nameof(name));
            }

            if (request.Arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Arguments must be a JSON object.", nameof(request));
            }

            var arguments = request.Arguments
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value.Clone());

            return await client.InvokeToolAsync(name.Trim(), arguments, cancellationToken);
        });

        return endpoints;
    }
}
