using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KaungMcp.Plugin;
using KaungMcp.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Tests;

public sealed class ContentEngineHttpTests
{
    private sealed class EngineHandler(bool block = false, bool fail = false) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancellationRequests;
        public string? Uploaded;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(new string('k', 32), request.Headers.GetValues("X-Engine-Key").Single());
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref CancellationRequests);
                return new(HttpStatusCode.Accepted);
            }
            Uploaded = await request.Content!.ReadAsStringAsync(cancellationToken);
            Started.TrySetResult();
            if (block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (fail) return new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { detail = "文件损坏测试" }) };
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new { summary = "完成", files = new[] { new { name = "result.md", mimeType = "text/markdown" } }, warnings = new[] { "样例警告" } }));
                using (var writer = new StreamWriter(archive.CreateEntry("result.md").Open())) writer.Write("# 测试结果");
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
        }
    }
    private static McpWebApplicationFactory Factory(EngineHandler handler, int timeout = 7200) => new(
        services => services.AddHttpClient("ContentEngine").ConfigurePrimaryHttpMessageHandler(() => handler),
        pluginIds: ["content"], configureConfiguration: builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Content:EngineKey"] = new string('k', 32), ["Content:EngineEndpoint"] = "http://engine.invalid",
            ["Content:JobTimeoutSeconds"] = timeout.ToString()
        }));
    private static async Task<Guid> Submit(McpWebApplicationFactory factory)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("hello 中文"));
        var file = await factory.Services.GetRequiredService<IContentFileStore>().UploadAsync(input, "test.txt", default);
        var id = await factory.Services.GetRequiredService<IPluginJobService>().SubmitJobAsync("content", "document",
            JsonSerializer.SerializeToElement(new { fileId = file.FileId }), default);
        return id;
    }
    private static IDisposable AsAdmin(McpWebApplicationFactory factory)
    {
        _ = factory.CreateClient();
        return factory.Services.GetRequiredService<IPluginExecutionContextAccessor>().Push(new("admin", "administrator"));
    }
    private static async Task<ContentJobInfo> Wait(McpWebApplicationFactory factory, Guid id)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await factory.Services.GetRequiredService<IPluginJobService>().GetJobAsync(id, default);
            if (job.Status is not ("queued" or "running")) return job;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }
    [Fact]
    public async Task Engine_contract_streams_input_and_registers_returned_zip_artifacts()
    {
        var engine = new EngineHandler(); await using var factory = Factory(engine);
        using var identity = AsAdmin(factory); var submitted = await Submit(factory);
        await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
        var job = await Wait(factory, submitted);
        Assert.Equal("succeeded", job.Status);
        Assert.Contains("hello 中文", engine.Uploaded);
        Assert.Contains("maxPdfPages", engine.Uploaded);
        Assert.Equal("样例警告", job.Result!.Value.GetProperty("warnings")[0].GetString());
        var output = job.Result.Value.GetProperty("files")[0].GetProperty("fileId").GetString()!;
        Assert.Equal("# 测试结果", (await factory.Services.GetRequiredService<IContentFileStore>().ReadTextAsync(output, 0, 8000, default)).Content);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_timeout_send_explicit_engine_stop(bool timeout)
    {
        var engine = new EngineHandler(block: true); await using var factory = Factory(engine, timeout ? 1 : 7200);
        using var identity = AsAdmin(factory); var submitted = await Submit(factory);
        await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (!timeout) await factory.Services.GetRequiredService<IPluginJobService>().CancelJobAsync(submitted, default);
        Assert.Equal(timeout ? "timed_out" : "cancelled", (await Wait(factory, submitted)).Status);
        Assert.Equal(1, engine.CancellationRequests);
    }
    [Fact]
    public async Task Engine_failure_remains_a_failed_job_with_public_error()
    {
        var engine = new EngineHandler(fail: true); await using var factory = Factory(engine);
        using var identity = AsAdmin(factory); var submitted = await Submit(factory);
        await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
        var job = await Wait(factory, submitted);
        Assert.Equal("failed", job.Status);
        Assert.Contains("文件损坏测试", job.Error);
    }
}
