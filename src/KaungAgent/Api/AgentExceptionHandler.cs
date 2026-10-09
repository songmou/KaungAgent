using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using KaungAgent.Models;
using ModelContextProtocol;

namespace KaungAgent.Api;

public sealed class AgentExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<AgentExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            ArgumentException => (
                StatusCodes.Status400BadRequest,
                "Invalid request",
                exception.Message),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "Resource not found",
                exception.Message),
            InvalidOperationException => (
                StatusCodes.Status409Conflict,
                "Operation is not allowed",
                exception.Message),
            McpProtocolException protocolException when protocolException.ErrorCode == McpErrorCode.InvalidParams => (
                StatusCodes.Status404NotFound,
                "MCP tool not found",
                protocolException.Message),
            ModelServiceException modelException => (
                StatusCodes.Status502BadGateway,
                "Model request failed",
                modelException.Message),
            HttpRequestException requestException when requestException.StatusCode is
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => (
                    StatusCodes.Status502BadGateway,
                    "MCP authentication failed",
                    "KaungAgent could not authenticate with the configured MCP server."),
            HttpRequestException => (
                StatusCodes.Status503ServiceUnavailable,
                "MCP server unavailable",
                "KaungAgent could not connect to the configured MCP server."),
            TimeoutException => (
                StatusCodes.Status504GatewayTimeout,
                "MCP request timed out",
                "The configured MCP server did not respond in time."),
            OperationCanceledException when !httpContext.RequestAborted.IsCancellationRequested => (
                StatusCodes.Status504GatewayTimeout,
                "MCP request timed out",
                "The configured MCP server did not respond in time."),
            McpException => (
                StatusCodes.Status502BadGateway,
                "MCP request failed",
                "The MCP server rejected or failed the request."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                null)
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "An unhandled KaungAgent API exception occurred.");
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail
            },
            Exception = exception
        });
    }
}
