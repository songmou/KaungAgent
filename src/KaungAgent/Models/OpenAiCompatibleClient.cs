using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KaungAgent.Data.Entities;
using KaungAgent.Security;

namespace KaungAgent.Models;

public sealed record ChatRequestMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<ModelToolCall>? ToolCalls = null);

public sealed record ModelToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ChatCompletionOutcome(
    string Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    string? FinishReason);

public sealed class ModelServiceException : HttpRequestException
{
    private static readonly Regex MaximumContextRegex = new(
        @"maximum context length is\s+(?<value>[\d,]+)\s+tokens",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RequestedTokensRegex = new(
        @"requested\s+(?<value>[\d,]+)\s+tokens",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MessageTokensRegex = new(
        @"(?<value>[\d,]+)\s+in (?:the )?messages",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CompletionTokensRegex = new(
        @"(?<value>[\d,]+)\s+in (?:the )?completion",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private ModelServiceException(
        HttpStatusCode statusCode,
        string responseBody,
        string? errorCode,
        string? errorMessage)
        : base($"模型服务返回 {(int)statusCode}: {TrimError(responseBody)}", null, statusCode)
    {
        ResponseBody = responseBody;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        var searchable = string.Join(
            ' ',
            new[] { errorCode, errorMessage, responseBody }.Where(value => !string.IsNullOrWhiteSpace(value)));
        IsContextLengthExceeded = (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge) &&
            (string.Equals(errorCode, "context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
             searchable.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
             searchable.Contains("context length exceeded", StringComparison.OrdinalIgnoreCase) ||
             searchable.Contains("context window", StringComparison.OrdinalIgnoreCase) &&
             (searchable.Contains("exceed", StringComparison.OrdinalIgnoreCase) ||
              searchable.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
              searchable.Contains("too many token", StringComparison.OrdinalIgnoreCase)));
        MaximumContextTokens = ReadTokenCount(MaximumContextRegex, searchable);
        RequestedTokens = ReadTokenCount(RequestedTokensRegex, searchable);
        MessageTokens = ReadTokenCount(MessageTokensRegex, searchable);
        CompletionTokens = ReadTokenCount(CompletionTokensRegex, searchable);
    }

    public string ResponseBody { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public bool IsContextLengthExceeded { get; }
    public long? MaximumContextTokens { get; }
    public long? RequestedTokens { get; }
    public long? MessageTokens { get; }
    public long? CompletionTokens { get; }

    internal static ModelServiceException Create(HttpStatusCode statusCode, string responseBody)
    {
        string? errorCode = null;
        string? errorMessage = null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                errorCode = ReadString(error, "code");
                errorMessage = ReadString(error, "message");
            }
            else
            {
                errorCode = ReadString(root, "code");
                errorMessage = ReadString(root, "message");
            }
        }
        catch (JsonException)
        {
            // OpenAI-compatible services do not always return JSON errors.
        }

        return new(statusCode, responseBody, errorCode, errorMessage);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadTokenCount(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success && long.TryParse(match.Groups["value"].Value.Replace(",", ""), out var parsed)
            ? parsed
            : null;
    }

    private static string TrimError(string body) => body.Length <= 600 ? body : body[..600] + "…";
}

public sealed class OpenAiCompatibleClient(
    IHttpClientFactory httpClientFactory,
    ModelProfileServiceAccessor profileAccessor)
{
    public async Task<ChatCompletionOutcome> CompleteAsync(
        ModelProfile profile,
        IReadOnlyList<ChatRequestMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        Func<string, Task> onText,
        CancellationToken cancellationToken,
        int? maxOutputTokensOverride = null,
        double? temperatureOverride = null)
    {
        var (apiKey, headers) = profileAccessor.Reveal(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));

        var client = httpClientFactory.CreateClient("OpenAiCompatible");
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(profile.BaseUrl));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        foreach (var header in headers)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = new StringContent(
            BuildRequestJson(profile, messages, tools, maxOutputTokensOverride, temperatureOverride),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            throw ModelServiceException.Create(response.StatusCode, body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCallBuilder>();
        long? input = null, output = null, total = null;
        string? finishReason = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = ReadInt64(usage, "prompt_tokens");
                output = ReadInt64(usage, "completion_tokens");
                total = ReadInt64(usage, "total_tokens");
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                finishReason = finish.GetString();
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var chunk = content.GetString() ?? "";
                text.Append(chunk);
                await onText(chunk);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var index = toolCall.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : calls.Count;
                    if (!calls.TryGetValue(index, out var builder)) calls[index] = builder = new();
                    if (toolCall.TryGetProperty("id", out var id)) builder.Id ??= id.GetString();
                    if (toolCall.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var name)) builder.Name ??= name.GetString();
                        if (function.TryGetProperty("arguments", out var arguments)) builder.Arguments.Append(arguments.GetString());
                    }
                }
            }
        }

        return new(
            text.ToString(),
            calls.Values.Select((call, index) => new ModelToolCall(
                call.Id ?? $"call_{index}", call.Name ?? "unknown", call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString())).ToList(),
            input, output, total, finishReason);
    }

    private static string BuildRequestJson(
        ModelProfile profile,
        IReadOnlyList<ChatRequestMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        int? maxOutputTokensOverride,
        double? temperatureOverride)
    {
        var messageNodes = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject { ["role"] = message.Role };
            if (message.Content is not null) node["content"] = message.Content;
            if (message.ToolCallId is not null) node["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls is { Count: > 0 })
            {
                node["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson }
                }).ToArray());
            }
            messageNodes.Add(node);
        }

        var root = new JsonObject
        {
            ["model"] = profile.ModelId,
            ["messages"] = messageNodes,
            ["temperature"] = temperatureOverride ?? profile.Temperature,
            ["max_tokens"] = maxOutputTokensOverride ?? profile.MaxOutputTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (tools.Count > 0)
        {
            root["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
                }
            }).ToArray());
            root["tool_choice"] = "auto";
        }
        return root.ToJsonString();
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var value = baseUrl.TrimEnd('/');
        if (!value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            value += "/chat/completions";
        return new Uri(value);
    }

    private static long? ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

// Breaks the ModelProfileService/OpenAiCompatibleClient construction cycle while keeping secret access internal.
public sealed class ModelProfileServiceAccessor(ProtectedSecretStore secrets)
{
    public (string? ApiKey, IReadOnlyDictionary<string, string> Headers) Reveal(ModelProfile profile) =>
        (secrets.Unprotect(profile.ProtectedApiKey), secrets.UnprotectHeaders(profile.ProtectedHeadersJson));
}
