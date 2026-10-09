using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KaungAgent.Data;
using KaungAgent.Data.Entities;
using KaungAgent.Mcp;
using KaungAgent.Models;
using Microsoft.EntityFrameworkCore;

namespace KaungAgent.Chat;

public sealed class ChatOrchestrator(
    IDbContextFactory<KaungAgentDbContext> dbFactory,
    ConversationService conversations,
    McpRegistryService mcpRegistry,
    OpenAiCompatibleClient chatClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ContextSummaryStatus = "context-summary";
    private const int MaximumCompressionAttempts = 2;
    private const int MaximumSummaryOutputTokens = 2048;
    private const string SummarySystemPrompt = """
        你是对话上下文压缩器。请忠实压缩提供的历史内容，以便另一个模型继续当前任务。
        必须保留：用户目标、已确认事实、重要名称与数值、已经作出的决定、约束条件、未完成事项，以及重要的工具调用结果或错误。
        不得编造信息，不得回答原始问题，不得省略会改变后续行为的细节。使用结构清晰、紧凑的 Markdown 输出摘要。
        """;

    public async Task SendAsync(
        Guid conversationId, string userText, Func<ChatStreamEvent, Task> publish, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) throw new ArgumentException("消息不能为空。");
        await conversations.AddMessageAsync(conversationId, "user", userText.Trim(), cancellationToken: cancellationToken);
        await RunAsync(conversationId, publish, cancellationToken);
    }

    public async Task RetryAsync(Guid conversationId, Func<ChatStreamEvent, Task> publish, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lastUserId = await db.ChatMessages.Where(item => item.ConversationId == conversationId && item.Role == "user")
            .MaxAsync(item => (long?)item.Id, cancellationToken)
            ?? throw new InvalidOperationException("当前会话还没有可重试的用户消息。");
        await db.ChatMessages.Where(item => item.ConversationId == conversationId && item.Id > lastUserId && item.Status != ContextSummaryStatus)
            .ExecuteDeleteAsync(cancellationToken);
        await RunAsync(conversationId, publish, cancellationToken);
    }

    private async Task RunAsync(Guid conversationId, Func<ChatStreamEvent, Task> publish, CancellationToken cancellationToken)
    {
        await using var initialDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await initialDb.Conversations.AsNoTracking()
            .Include(item => item.ModelProfile)
            .Include(item => item.Messages.OrderBy(message => message.Id))
            .SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken)
            ?? throw new KeyNotFoundException("会话不存在。");
        var model = conversation.ModelProfile;
        if (model is null || !model.IsEnabled) throw new InvalidOperationException("当前会话的模型不存在或已禁用。");

        var selections = JsonSerializer.Deserialize<List<ConversationToolSelection>>(conversation.ToolSelectionJson, JsonOptions) ?? [];
        var tools = await BuildToolsAsync(selections, cancellationToken);
        var requestMessages = BuildRequestMessages(conversation.Messages.OrderBy(message => message.Id).ToList());

        for (var round = 1; round <= 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            await publish(new("status", round == 1 ? "模型正在思考…" : $"正在继续第 {round} 轮推理…"));
            ChatCompletionOutcome outcome;
            var compressionAttempts = 0;
            while (true)
            {
                try
                {
                    outcome = await chatClient.CompleteAsync(
                        model, requestMessages, tools.Definitions,
                        chunk => publish(new("delta", chunk)), cancellationToken);
                    break;
                }
                catch (ModelServiceException exception) when (exception.IsContextLengthExceeded)
                {
                    if (compressionAttempts >= MaximumCompressionAttempts)
                    {
                        throw new InvalidOperationException(
                            "自动压缩上下文两次后仍超过模型限制，请缩短当前消息或新建会话。",
                            exception);
                    }

                    compressionAttempts++;
                    await publish(new("status", "上下文过长，正在压缩历史消息…"));
                    requestMessages = await CompressContextAsync(
                        conversationId, model, exception, cancellationToken);
                    await publish(new("status", "上下文已压缩，正在重新请求模型…"));
                    watch.Restart();
                }
            }

            var assistant = new ChatMessage
            {
                ConversationId = conversationId, Role = "assistant", Content = outcome.Text,
                ToolCallsJson = outcome.ToolCalls.Count == 0 ? null : JsonSerializer.Serialize(outcome.ToolCalls, JsonOptions),
                Status = "completed", InputTokens = outcome.InputTokens, OutputTokens = outcome.OutputTokens,
                TotalTokens = outcome.TotalTokens, DurationMilliseconds = watch.ElapsedMilliseconds, CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                db.ChatMessages.Add(assistant);
                var trackedConversation = await db.Conversations.FindAsync([conversationId], cancellationToken);
                if (trackedConversation is not null) trackedConversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            requestMessages.Add(new ChatRequestMessage("assistant", outcome.Text, ToolCalls: outcome.ToolCalls));
            if (outcome.ToolCalls.Count == 0)
            {
                await publish(new("completed", outcome.Text));
                return;
            }

            foreach (var call in outcome.ToolCalls)
            {
                if (!tools.Map.TryGetValue(call.Name, out var target))
                {
                    var missing = $"未找到工具映射：{call.Name}";
                    await SaveToolResultAsync(assistant.Id, conversationId, Guid.Empty, call, false, null, missing, 0, cancellationToken);
                    requestMessages.Add(new("tool", missing, call.Id));
                    await publish(new("tool", ToolName: call.Name, Succeeded: false, Error: missing));
                    continue;
                }

                await publish(new("tool-start", ToolName: target.ToolName));
                var arguments = ParseArguments(call.ArgumentsJson);
                var result = await mcpRegistry.InvokeToolAsync(target.ServerId, target.ToolName, arguments, cancellationToken);
                var resultJson = result.Result.GetRawText();
                await SaveToolResultAsync(
                    assistant.Id, conversationId, target.ServerId, call, result.Succeeded,
                    resultJson, result.Error, result.DurationMilliseconds, cancellationToken);
                requestMessages.Add(new("tool", resultJson, call.Id));
                await publish(new("tool", ToolName: target.ToolName, Succeeded: result.Succeeded, Error: result.Error));
            }
        }

        var limitMessage = "已达到单次对话允许的 8 轮工具调用上限，请缩小问题范围后重试。";
        await conversations.AddMessageAsync(conversationId, "assistant", limitMessage, "limit", cancellationToken);
        await publish(new("error", Error: limitMessage));
    }

    private async Task<(List<ModelToolDefinition> Definitions, Dictionary<string, ToolTarget> Map)> BuildToolsAsync(
        IReadOnlyList<ConversationToolSelection> selections, CancellationToken cancellationToken)
    {
        var definitions = new List<ModelToolDefinition>();
        var map = new Dictionary<string, ToolTarget>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            McpServerResponse? server;
            try
            {
                await mcpRegistry.RefreshAsync(selection.ServerId, cancellationToken);
                server = await mcpRegistry.GetAsync(selection.ServerId, cancellationToken);
            }
            catch
            {
                continue;
            }
            if (server is not { IsEnabled: true, LastStatus: "online", Snapshot: not null }) continue;
            foreach (var tool in server.Snapshot.Tools)
            {
                if (selection.Tools is { Count: > 0 } && !selection.Tools.Contains(tool.Name)) continue;
                var functionName = BuildFunctionName(server.Id, tool.Name);
                var parameters = tool.Definition.TryGetProperty("inputSchema", out var schema)
                    ? schema.Clone() : JsonSerializer.SerializeToElement(new { type = "object" });
                definitions.Add(new(functionName, tool.Description ?? tool.Title ?? tool.Name, parameters));
                map[functionName] = new(server.Id, tool.Name);
            }
        }
        return (definitions, map);
    }

    private static ChatRequestMessage ToRequestMessage(ChatMessage message)
    {
        var calls = string.IsNullOrWhiteSpace(message.ToolCallsJson)
            ? null : JsonSerializer.Deserialize<List<ModelToolCall>>(message.ToolCallsJson, JsonOptions);
        return new(message.Role, message.Content, message.ToolCallId, calls);
    }

    private static List<ChatRequestMessage> BuildRequestMessages(IReadOnlyList<ChatMessage> messages)
    {
        var summary = messages.LastOrDefault(message =>
            message.Status == ContextSummaryStatus && message.SummarizesThroughMessageId is not null);
        var cutoff = summary?.SummarizesThroughMessageId ?? 0;
        var result = new List<ChatRequestMessage>();
        if (summary is not null)
        {
            result.Add(new ChatRequestMessage(
                "system",
                $"以下是此前对话的压缩摘要。将其作为事实和任务上下文继续对话：\n\n{summary.Content}"));
        }

        result.AddRange(messages
            .Where(message => message.Id > cutoff && message.Status != ContextSummaryStatus)
            .Select(ToRequestMessage));
        return result;
    }

    private async Task<List<ChatRequestMessage>> CompressContextAsync(
        Guid conversationId,
        ModelProfile model,
        ModelServiceException limitError,
        CancellationToken cancellationToken)
    {
        await using var lookup = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await lookup.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Id)
            .ToListAsync(cancellationToken);

        var previousSummary = messages.LastOrDefault(message =>
            message.Status == ContextSummaryStatus && message.SummarizesThroughMessageId is not null);
        var previousCutoff = previousSummary?.SummarizesThroughMessageId ?? 0;
        var rawMessages = messages
            .Where(message => message.Id > previousCutoff && message.Status != ContextSummaryStatus)
            .ToList();
        var groups = GroupIntoUserTurns(rawMessages);
        var currentTurnIndex = groups.FindLastIndex(group => group.Any(message => message.Role == "user"));
        if (currentTurnIndex <= 0)
        {
            throw new InvalidOperationException(
                "当前消息或最近一轮对话本身已经超过模型上下文限制，无法通过压缩更早历史恢复。",
                limitError);
        }

        var compressibleGroups = groups.Take(currentTurnIndex).ToList();
        var selectedGroupCount = SelectGroupsToCompress(
            compressibleGroups,
            rawMessages,
            previousSummary,
            model.MaxOutputTokens,
            limitError);
        if (selectedGroupCount == 0)
        {
            throw new InvalidOperationException("没有可安全压缩的历史轮次。", limitError);
        }

        var selectedMessages = compressibleGroups.Take(selectedGroupCount).SelectMany(group => group).ToList();
        var summarizesThroughId = selectedMessages[^1].Id;
        var transcriptParts = new List<string>();
        if (previousSummary is not null)
        {
            transcriptParts.Add($"[此前已有摘要]\n{previousSummary.Content}");
        }
        transcriptParts.AddRange(selectedMessages.Select(FormatForSummary));

        var maximumContextTokens = limitError.MaximumContextTokens ?? 32_768;
        var chunkByteLimit = (int)Math.Clamp(maximumContextTokens * 2 / 5, 512, 400_000);
        var summaryOutputTokens = Math.Min(
            Math.Min(MaximumSummaryOutputTokens, model.MaxOutputTokens),
            (int)Math.Clamp(maximumContextTokens / 10, 128, MaximumSummaryOutputTokens));
        var usage = new CompressionUsage();
        string summary;
        try
        {
            var chunks = BuildChunks(transcriptParts, chunkByteLimit);
            var summaries = new List<string>(chunks.Count);
            foreach (var chunk in chunks)
            {
                summaries.Add(await GenerateSummaryAsync(
                    model, chunk, summaryOutputTokens, usage, cancellationToken));
            }

            while (summaries.Count > 1)
            {
                var mergeParts = summaries.Select((value, index) => $"[分段摘要 {index + 1}]\n{value}").ToList();
                var mergeChunks = BuildChunks(mergeParts, chunkByteLimit);
                var merged = new List<string>(mergeChunks.Count);
                foreach (var chunk in mergeChunks)
                {
                    merged.Add(await GenerateSummaryAsync(
                        model,
                        $"请将以下分段摘要合并为一份无重复、信息完整的上下文摘要：\n\n{chunk}",
                        summaryOutputTokens,
                        usage,
                        cancellationToken));
                }
                if (merged.Count >= summaries.Count)
                {
                    throw new InvalidOperationException("摘要分段无法继续合并，请缩短当前消息或新建会话。");
                }
                summaries = merged;
            }

            summary = summaries.SingleOrDefault()?.Trim() ?? "";
        }
        catch (ModelServiceException exception) when (exception.IsContextLengthExceeded)
        {
            throw new InvalidOperationException("生成上下文摘要时仍超过模型限制，请缩短当前消息或新建会话。", exception);
        }

        if (summary.Length == 0)
        {
            throw new InvalidOperationException("模型未能生成上下文摘要，请稍后重试。");
        }

        var now = DateTimeOffset.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ConversationId = conversationId,
                Role = "system",
                Content = summary,
                Status = ContextSummaryStatus,
                SummarizesThroughMessageId = summarizesThroughId,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens,
                DurationMilliseconds = usage.DurationMilliseconds,
                CreatedAtUtc = now
            });
            var trackedConversation = await db.Conversations.FindAsync([conversationId], cancellationToken);
            if (trackedConversation is not null) trackedConversation.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        await using var reload = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updatedMessages = await reload.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Id)
            .ToListAsync(cancellationToken);
        return BuildRequestMessages(updatedMessages);
    }

    private async Task<string> GenerateSummaryAsync(
        ModelProfile model,
        string transcript,
        int maxOutputTokens,
        CompressionUsage usage,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var outcome = await chatClient.CompleteAsync(
            model,
            [new ChatRequestMessage("system", SummarySystemPrompt), new ChatRequestMessage("user", transcript)],
            [],
            _ => Task.CompletedTask,
            cancellationToken,
            maxOutputTokens,
            temperatureOverride: 0);
        watch.Stop();
        usage.Add(outcome, watch.ElapsedMilliseconds);
        return outcome.Text;
    }

    private static List<List<ChatMessage>> GroupIntoUserTurns(IReadOnlyList<ChatMessage> messages)
    {
        var groups = new List<List<ChatMessage>>();
        foreach (var message in messages)
        {
            if (message.Role == "user" || groups.Count == 0)
            {
                groups.Add([]);
            }
            groups[^1].Add(message);
        }
        return groups;
    }

    private static int SelectGroupsToCompress(
        IReadOnlyList<List<ChatMessage>> compressibleGroups,
        IReadOnlyList<ChatMessage> rawMessages,
        ChatMessage? previousSummary,
        int maximumOutputTokens,
        ModelServiceException limitError)
    {
        var messageTokens = limitError.MessageTokens ??
            (limitError.RequestedTokens is { } requested
                ? Math.Max(0, requested - (limitError.CompletionTokens ?? maximumOutputTokens))
                : null);
        if (messageTokens is null || limitError.MaximumContextTokens is null)
        {
            return Math.Max(1, (compressibleGroups.Count + 1) / 2);
        }

        var targetMessageTokens = Math.Max(
            1,
            (long)Math.Floor(limitError.MaximumContextTokens.Value * 0.8) - maximumOutputTokens);
        var requiredReduction = Math.Max(1, messageTokens.Value - targetMessageTokens) + MaximumSummaryOutputTokens;
        var allWeight = rawMessages.Sum(EstimateMessageWeight) +
            (previousSummary is null ? 0 : EstimateMessageWeight(previousSummary));
        if (allWeight <= 0) return 1;

        long selectedWeight = 0;
        for (var index = 0; index < compressibleGroups.Count; index++)
        {
            selectedWeight += compressibleGroups[index].Sum(EstimateMessageWeight);
            var estimatedReduction = (long)Math.Floor(messageTokens.Value * (double)selectedWeight / allWeight);
            if (estimatedReduction >= requiredReduction) return index + 1;
        }
        return compressibleGroups.Count;
    }

    private static long EstimateMessageWeight(ChatMessage message) =>
        Encoding.UTF8.GetByteCount(message.Role) +
        Encoding.UTF8.GetByteCount(message.Content ?? "") +
        Encoding.UTF8.GetByteCount(message.ToolCallsJson ?? "") +
        Encoding.UTF8.GetByteCount(message.ToolCallId ?? "") + 32;

    private static string FormatForSummary(ChatMessage message)
    {
        var builder = new StringBuilder();
        builder.Append("[消息 ").Append(message.Id).Append(" | ").Append(message.Role);
        if (!string.IsNullOrWhiteSpace(message.ToolName)) builder.Append(" | 工具 ").Append(message.ToolName);
        builder.AppendLine("]");
        if (!string.IsNullOrWhiteSpace(message.Content)) builder.AppendLine(message.Content);
        if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
            builder.Append("工具调用：").AppendLine(message.ToolCallsJson);
        return builder.ToString();
    }

    private static List<string> BuildChunks(IReadOnlyList<string> parts, int maximumBytes)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentBytes = 0;
        foreach (var part in parts.SelectMany(part => SplitByUtf8Bytes(part, maximumBytes)))
        {
            var partBytes = Encoding.UTF8.GetByteCount(part);
            if (currentBytes > 0 && currentBytes + partBytes + 2 > maximumBytes)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentBytes = 0;
            }
            if (currentBytes > 0)
            {
                current.AppendLine().AppendLine();
                currentBytes += 2;
            }
            current.Append(part);
            currentBytes += partBytes;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static IEnumerable<string> SplitByUtf8Bytes(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            yield return value;
            yield break;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes > 0 && bytes + rune.Utf8SequenceLength > maximumBytes)
            {
                yield return builder.ToString();
                builder.Clear();
                bytes = 0;
            }
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    private async Task SaveToolResultAsync(
        long assistantMessageId, Guid conversationId, Guid serverId, ModelToolCall call, bool succeeded,
        string? resultJson, string? error, long duration, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.ToolExecutions.Add(new ToolExecution
        {
            ChatMessageId = assistantMessageId, McpServerId = serverId, ToolCallId = call.Id,
            ToolName = call.Name, ArgumentsJson = call.ArgumentsJson, ResultJson = resultJson,
            Succeeded = succeeded, Error = error, DurationMilliseconds = duration, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = conversationId, Role = "tool", Content = resultJson ?? error,
            ToolCallId = call.Id, ToolName = call.Name, Status = succeeded ? "completed" : "error",
            Error = error, DurationMilliseconds = duration, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("模型工具参数不是 JSON 对象。");
        return document.RootElement.EnumerateObject().ToDictionary(
            item => item.Name, item => (object?)item.Value.Clone());
    }

    private static string BuildFunctionName(Guid serverId, string toolName)
    {
        var safe = Regex.Replace(toolName, "[^A-Za-z0-9_-]", "_");
        if (safe.Length > 45) safe = safe[..45];
        return $"mcp_{serverId:N}"[..12] + "_" + safe;
    }

    private sealed record ToolTarget(Guid ServerId, string ToolName);

    private sealed class CompressionUsage
    {
        public long? InputTokens { get; private set; }
        public long? OutputTokens { get; private set; }
        public long? TotalTokens { get; private set; }
        public long DurationMilliseconds { get; private set; }

        public void Add(ChatCompletionOutcome outcome, long durationMilliseconds)
        {
            InputTokens = AddNullable(InputTokens, outcome.InputTokens);
            OutputTokens = AddNullable(OutputTokens, outcome.OutputTokens);
            TotalTokens = AddNullable(TotalTokens, outcome.TotalTokens);
            DurationMilliseconds += durationMilliseconds;
        }

        private static long? AddNullable(long? left, long? right) =>
            left is null && right is null ? null : left.GetValueOrDefault() + right.GetValueOrDefault();
    }
}
