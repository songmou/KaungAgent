using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KaungAgent.Chat;
using KaungAgent.Data;
using KaungAgent.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KaungAgent.Tests;

public sealed class ContextCompressionTests
{
    private const string ContextLimitBody = """
        {"error":{"message":"This model's maximum context length is 1048576 tokens. However, you requested 1269334 tokens (1265238 in the messages, 4096 in the completion). Please reduce the length of the messages or completion.","type":"invalid_request_error","param":null,"code":"invalid_request_error"}}
        """;

    [Fact]
    public void Context_limit_error_is_classified_and_token_counts_are_extracted()
    {
        var exception = ModelServiceException.Create(HttpStatusCode.BadRequest, ContextLimitBody);

        Assert.True(exception.IsContextLengthExceeded);
        Assert.Equal("invalid_request_error", exception.ErrorCode);
        Assert.Equal(1_048_576, exception.MaximumContextTokens);
        Assert.Equal(1_269_334, exception.RequestedTokens);
        Assert.Equal(1_265_238, exception.MessageTokens);
        Assert.Equal(4_096, exception.CompletionTokens);
    }

    [Fact]
    public void Unrelated_bad_request_is_not_classified_as_context_limit()
    {
        const string body = "{\"error\":{\"message\":\"temperature must be between 0 and 2\",\"code\":\"invalid_request_error\"}}";

        var exception = ModelServiceException.Create(HttpStatusCode.BadRequest, body);

        Assert.False(exception.IsContextLengthExceeded);
        Assert.Null(exception.MaximumContextTokens);
    }

    [Fact]
    public async Task Context_limit_creates_visible_summary_retries_and_reuses_checkpoint()
    {
        const string oldMarker = "OLD_HISTORY_MARKER";
        const string latestMessage = "请基于最近上下文继续。";
        const string summaryText = "- 已保留旧任务目标和关键结论。";
        var modelHandler = new SequenceModelHandler(
            Error(HttpStatusCode.BadRequest, ContextLimitBody),
            Stream(summaryText, 600, 120, 720),
            Stream("首次恢复成功。", 800, 30, 830),
            Stream("重新生成成功。", 810, 25, 835));
        using var mcpHandler = new PassiveHandler();
        await using var factory = new AgentWebApplicationFactory(
            mcpHandler,
            "kmcp_context-compression-tests",
            modelHandler: modelHandler);

        await using var scope = factory.Services.CreateAsyncScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestrator>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KaungAgentDbContext>>();
        var model = await models.SaveAsync(null, new SaveModelProfileRequest
        {
            Name = "context-test-model",
            BaseUrl = "https://model.test/v1",
            ModelId = "context-test",
            MaxOutputTokens = 4096,
            IsEnabled = true,
            IsDefault = true
        });
        var conversation = await conversations.CreateAsync(model.Id, []);
        var oldUser = await conversations.AddMessageAsync(conversation.Id, "user", oldMarker);
        await conversations.AddMessageAsync(conversation.Id, "assistant", "旧回复内容");
        var oldToolResult = await conversations.AddMessageAsync(conversation.Id, "tool", "TOOL_RESULT_MARKER");
        var events = new List<ChatStreamEvent>();

        await orchestrator.SendAsync(
            conversation.Id,
            latestMessage,
            item => { events.Add(item); return Task.CompletedTask; });

        var bodies = modelHandler.RequestBodies.ToArray();
        Assert.Equal(3, bodies.Length);
        Assert.Contains(oldMarker, bodies[0], StringComparison.Ordinal);
        using (var summaryRequest = JsonDocument.Parse(bodies[1]))
        {
            Assert.Equal(0, summaryRequest.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(2048, summaryRequest.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(summaryRequest.RootElement.TryGetProperty("tools", out _));
            Assert.Contains("TOOL_RESULT_MARKER", RequestMessageContents(summaryRequest.RootElement), StringComparison.Ordinal);
        }
        using (var retriedRequest = JsonDocument.Parse(bodies[2]))
        {
            var messages = retriedRequest.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains(summaryText, messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(oldMarker, bodies[2], StringComparison.Ordinal);
            Assert.Contains(latestMessage, RequestMessageContents(retriedRequest.RootElement), StringComparison.Ordinal);
        }
        Assert.Contains(events, item => item.Type == "status" && item.Text == "上下文过长，正在压缩历史消息…");

        var detail = await conversations.GetAsync(conversation.Id);
        Assert.NotNull(detail);
        Assert.Contains(detail.Messages, message => message.Content == oldMarker);
        var visibleSummary = Assert.Single(detail.Messages, message => message.Role == "system" && message.Status == "context-summary");
        Assert.Equal(summaryText, visibleSummary.Content);
        Assert.Equal(720, visibleSummary.TotalTokens);
        Assert.Contains(detail.Messages, message => message.Content == "首次恢复成功。");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var storedSummary = await db.ChatMessages.AsNoTracking()
                .SingleAsync(message => message.ConversationId == conversation.Id && message.Status == "context-summary");
            Assert.Equal(oldToolResult.Id, storedSummary.SummarizesThroughMessageId);
        }

        await orchestrator.RetryAsync(conversation.Id, _ => Task.CompletedTask);

        var retryBodies = modelHandler.RequestBodies.ToArray();
        Assert.Equal(4, retryBodies.Length);
        using var regeneratedRequest = JsonDocument.Parse(retryBodies[3]);
        Assert.Contains(summaryText, RequestMessageContents(regeneratedRequest.RootElement), StringComparison.Ordinal);
        Assert.DoesNotContain(oldMarker, retryBodies[3], StringComparison.Ordinal);
        var retriedDetail = await conversations.GetAsync(conversation.Id);
        Assert.NotNull(retriedDetail);
        Assert.Single(retriedDetail.Messages, message => message.Status == "context-summary");
        Assert.DoesNotContain(retriedDetail.Messages, message => message.Content == "首次恢复成功。");
        Assert.Contains(retriedDetail.Messages, message => message.Content == "重新生成成功。");
        Assert.Equal(oldUser.Id, retriedDetail.Messages.Single(message => message.Content == oldMarker).Id);
    }

    [Fact]
    public async Task Unrelated_bad_request_is_returned_without_compression_retry()
    {
        const string body = "{\"error\":{\"message\":\"unknown parameter\",\"code\":\"invalid_request_error\"}}";
        var modelHandler = new SequenceModelHandler(Error(HttpStatusCode.BadRequest, body));
        using var mcpHandler = new PassiveHandler();
        await using var factory = new AgentWebApplicationFactory(
            mcpHandler,
            "kmcp_non-context-tests",
            modelHandler: modelHandler);

        await using var scope = factory.Services.CreateAsyncScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestrator>();
        var model = await models.SaveAsync(null, new SaveModelProfileRequest
        {
            Name = "bad-request-test-model",
            BaseUrl = "https://model.test/v1",
            ModelId = "bad-request-test",
            IsEnabled = true,
            IsDefault = true
        });
        var conversation = await conversations.CreateAsync(model.Id, []);
        await conversations.AddMessageAsync(conversation.Id, "user", "旧消息");
        await conversations.AddMessageAsync(conversation.Id, "assistant", "旧回复");

        var exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            orchestrator.SendAsync(conversation.Id, "新消息", _ => Task.CompletedTask));

        Assert.False(exception.IsContextLengthExceeded);
        Assert.Single(modelHandler.RequestBodies);
        var detail = await conversations.GetAsync(conversation.Id);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail.Messages, message => message.Status == "context-summary");
    }

    [Fact]
    public async Task Recovery_stops_after_two_compressions()
    {
        const string smallerLimitBody = """
            {"error":{"message":"This model's maximum context length is 100000 tokens. However, you requested 85000 tokens (84900 in the messages, 100 in the completion).","code":"context_length_exceeded"}}
            """;
        var modelHandler = new SequenceModelHandler(
            Error(HttpStatusCode.BadRequest, smallerLimitBody),
            Stream("第一次摘要", 1000, 50, 1050),
            Error(HttpStatusCode.BadRequest, smallerLimitBody),
            Stream("第二次摘要", 1000, 50, 1050),
            Error(HttpStatusCode.BadRequest, smallerLimitBody));
        using var mcpHandler = new PassiveHandler();
        await using var factory = new AgentWebApplicationFactory(
            mcpHandler,
            "kmcp_recovery-limit-tests",
            modelHandler: modelHandler);

        await using var scope = factory.Services.CreateAsyncScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestrator>();
        var model = await models.SaveAsync(null, new SaveModelProfileRequest
        {
            Name = "recovery-limit-test-model",
            BaseUrl = "https://model.test/v1",
            ModelId = "recovery-limit-test",
            MaxOutputTokens = 100,
            IsEnabled = true,
            IsDefault = true
        });
        var conversation = await conversations.CreateAsync(model.Id, []);
        for (var index = 0; index < 3; index++)
        {
            await conversations.AddMessageAsync(conversation.Id, "user", $"旧问题 {index}");
            await conversations.AddMessageAsync(conversation.Id, "assistant", $"旧回答 {index}");
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SendAsync(conversation.Id, "当前问题", _ => Task.CompletedTask));

        Assert.Contains("自动压缩上下文两次后仍超过模型限制", exception.Message, StringComparison.Ordinal);
        Assert.Equal(5, modelHandler.RequestBodies.Count);
        var detail = await conversations.GetAsync(conversation.Id);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Messages.Count(message => message.Status == "context-summary"));
    }

    [Fact]
    public async Task A_single_oversized_current_turn_returns_a_clear_error()
    {
        var modelHandler = new SequenceModelHandler(Error(HttpStatusCode.BadRequest, ContextLimitBody));
        using var mcpHandler = new PassiveHandler();
        await using var factory = new AgentWebApplicationFactory(
            mcpHandler,
            "kmcp_single-turn-tests",
            modelHandler: modelHandler);

        await using var scope = factory.Services.CreateAsyncScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestrator>();
        var model = await models.SaveAsync(null, new SaveModelProfileRequest
        {
            Name = "single-turn-test-model",
            BaseUrl = "https://model.test/v1",
            ModelId = "single-turn-test",
            IsEnabled = true,
            IsDefault = true
        });
        var conversation = await conversations.CreateAsync(model.Id, []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SendAsync(conversation.Id, "单条超长消息", _ => Task.CompletedTask));

        Assert.Contains("当前消息或最近一轮对话本身已经超过", exception.Message, StringComparison.Ordinal);
        Assert.Single(modelHandler.RequestBodies);
        var detail = await conversations.GetAsync(conversation.Id);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail.Messages, message => message.Status == "context-summary");
    }

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string RequestMessageContents(JsonElement request) => string.Join(
        "\n",
        request.GetProperty("messages").EnumerateArray()
            .Select(message => message.TryGetProperty("content", out var content) ? content.GetString() : null)
            .Where(content => content is not null));

    private static HttpResponseMessage Stream(string text, long input, long output, long total)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = input, completion_tokens = output, total_tokens = total }
        });
        var content = $"data: {payload}\n\ndata: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class SequenceModelHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new(responses);
        public ConcurrentQueue<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Enqueue(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("模型测试响应队列已耗尽。");
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class PassiveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }
}
