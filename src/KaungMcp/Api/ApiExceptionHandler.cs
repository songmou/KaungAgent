using KaungMcp.Clients;
using KaungMcp.Plugin;
using KaungMcp.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace KaungMcp.Api;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ClientApplicationNotFoundException => (StatusCodes.Status404NotFound, "MCP client not found"),
            ClientApplicationConflictException => (StatusCodes.Status409Conflict, "MCP client conflict"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ContentConflictException => (StatusCodes.Status409Conflict, "File or job conflict"),
            ContentLimitException => (StatusCodes.Status413PayloadTooLarge, "Content limit exceeded"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            WebFetchException => (StatusCodes.Status400BadRequest, "Invalid file URL"),
            TimeoutException => (StatusCodes.Status504GatewayTimeout, "Download timed out"),
            HttpRequestException => (StatusCodes.Status502BadGateway, "Remote service unavailable"),
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request"),
            InvalidDataException => (StatusCodes.Status400BadRequest, "Invalid file data"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "An unhandled API exception occurred.");
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status500InternalServerError ? null : exception.Message
            },
            Exception = exception
        });
    }
}
