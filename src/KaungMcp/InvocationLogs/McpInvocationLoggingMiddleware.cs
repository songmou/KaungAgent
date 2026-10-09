using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KaungMcp.Security;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol;

namespace KaungMcp.InvocationLogs;

/// <summary>Captures one authenticated MCP HTTP exchange without delaying streamed output.</summary>
public sealed class McpInvocationLoggingMiddleware(
    RequestDelegate next, ILogger<McpInvocationLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, McpInvocationLogService logs)
    {
        if (context.Request.Path != "/mcp" || !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var authentication = await context.AuthenticateAsync(SecurityConstants.McpClientScheme);
        if (!authentication.Succeeded ||
            !Guid.TryParse(authentication.Principal?.FindFirst(SecurityConstants.ClientIdClaim)?.Value, out var clientId))
        {
            await next(context);
            return;
        }

        var request = await ReadRequestAsync(context);
        if (request is null)
        {
            await next(context);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var originalBody = context.Response.Body;
        using var capture = new ResponseCaptureStream(originalBody, logger);
        context.Response.Body = capture;
        Exception? failure = null;
        try { await next(context); }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            timer.Stop();
            context.Response.Body = originalBody;
            // Logging must never replace an application exception or break a successful response.
            try
            {
                var body = ReadResponse(capture.GetBody(), context.Response.ContentType, request.Id);
                var toolException = context.Items[McpInvocationLoggingExtensions.ToolExceptionKey] as Exception;
                var (errorCode, errorMessage) = ClassifyFailure(body, failure ?? toolException, context.Response.StatusCode);
                await logs.WriteAsync(clientId, request.Method, request.Target, started,
                    timer.ElapsedMilliseconds, errorCode is null, errorCode, errorMessage,
                    request.Parameters, body);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to capture MCP invocation details.");
            }
        }
    }

    private async Task<CapturedRequest?> ReadRequestAsync(HttpContext context)
    {
        try
        {
            context.Request.EnableBuffering();
            using var document = await JsonDocument.ParseAsync(context.Request.Body,
                cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var id) ||
                id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                return null; // Notifications and malformed requests do not belong in client invocation logs.

            var parameters = root.TryGetProperty("params", out var value) ? value.GetRawText() : "null";
            string? target = null;
            if (value.ValueKind == JsonValueKind.Object)
            {
                var targetKey = method.GetString() == "resources/read" ? "uri" : "name";
                if (value.TryGetProperty(targetKey, out var name) && name.ValueKind == JsonValueKind.String)
                    target = name.GetString();
            }
            return new CapturedRequest(method.GetString()!, id.Clone(), parameters, target);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to capture MCP request parameters.");
            return null;
        }
        finally
        {
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
        }
    }

    private static string? ReadResponse(string? body, string? contentType, JsonElement requestId)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        if (contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) != true)
            return body;

        // SSE may also contain notifications. Retain only the response for this request ID.
        using var reader = new StringReader(body);
        var data = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                if (IsResponse(data.ToString(), requestId)) return data.ToString();
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5));
            }
        }
        return IsResponse(data.ToString(), requestId) ? data.ToString() : null;
    }

    private static bool IsResponse(string data, JsonElement requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) &&
                JsonElement.DeepEquals(id, requestId) &&
                (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
        }
        catch (JsonException) { return false; }
    }

    private static (string? Code, string? Message) ClassifyFailure(string? body, Exception? failure, int statusCode)
    {
        if (failure is OperationCanceledException) return ("cancelled", "The MCP request was cancelled.");
        if (failure is McpException) return ("mcp_error", "The MCP tool request failed.");
        if (failure is not null) return ("unhandled_exception", "The MCP request failed unexpectedly.");
        if (body is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        return ("mcp_error", error.TryGetProperty("message", out var message) &&
                            message.ValueKind == JsonValueKind.String ? message.GetString() : "The MCP request failed.");
                    if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                        result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                        return ("tool_error", "The tool returned an error result.");
                }
            }
            catch (JsonException) { }
        }
        return statusCode >= 400 ? ("request_error", $"The MCP request returned HTTP {statusCode}.") : (null, null);
    }

    private sealed record CapturedRequest(string Method, JsonElement Id, string Parameters, string? Target);

    private sealed class ResponseCaptureStream(Stream destination, ILogger logger) : Stream
    {
        private readonly MemoryStream captured = new();
        private bool captureFailed;
        public string? GetBody() => captureFailed ? null : Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length);
        private void Capture(ReadOnlySpan<byte> buffer)
        {
            if (captureFailed) return;
            try { captured.Write(buffer); }
            catch (Exception exception)
            {
                captureFailed = true;
                logger.LogWarning(exception, "Unable to capture MCP response body.");
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            destination.Write(buffer);
            Capture(buffer);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(buffer, cancellationToken);
            Capture(buffer.Span);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) captured.Dispose();
            base.Dispose(disposing); // The underlying HTTP stream belongs to the server.
        }
    }
}
