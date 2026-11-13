using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.Plugin;
using KaungMcp.Plugins.Content;
using KaungMcp.Scheduling;
using KaungMcp.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KaungMcp.Tests;

public sealed class ContentProcessingTests
{
    private sealed class EngineState
    {
        public bool Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExecutionIdentity? Identity;
    }
    private sealed class FakeParser(IContentFileStore files, IPluginExecutionContextAccessor execution, EngineState state) : IDocumentParser
    {
        public async Task<ContentProcessingResult> ParseAsync(Guid runId, JsonElement arguments, CancellationToken ct)
        {
            state.Identity = execution.Current;
            state.Started.TrySetResult();
            try { if (state.Block) await state.Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { state.Cancelled.TrySetResult(); throw; }
            await using var input = await files.OpenReadAsync(arguments.GetProperty("fileId").GetString()!, ct);
            using var reader = new StreamReader(input);
            var text = await reader.ReadToEndAsync(ct);
            using var output = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var artifact = await files.CreateArtifactAsync("content", "result.txt", "text/plain", output, ct);
            return new("解析完成", [artifact], []);
        }
    }
    private static McpWebApplicationFactory Factory(EngineState state) => new(services =>
    {
        services.AddSingleton(state);
        services.RemoveAll<IDocumentParser>();
        services.AddSingleton<IDocumentParser, FakeParser>();
    }, pluginIds: ["content"]);
    private static HttpClient Admin(McpWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Kaung-Admin-Key", McpWebApplicationFactory.AdminKey);
        return client;
    }
    private static async Task<(HttpClient Http, Guid Id, string Key)> Client(McpWebApplicationFactory factory, string name)
    {
        using var admin = Admin(factory);
        var response = await admin.PostAsJsonAsync("/api/admin/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreatedClientResponse>())!;
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.ApiKey);
        return (http, created.Id, created.ApiKey);
    }
    private static async Task<ContentFileInfo> Upload(HttpClient http, string text = "中文 hello", string name = "sample.txt", bool admin = false)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name);
        var response = await http.PostAsync(admin ? "/api/admin/files" : "/api/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ContentFileInfo>())!;
    }
    private static IDisposable As(McpWebApplicationFactory factory, Guid id) =>
        factory.Services.GetRequiredService<IPluginExecutionContextAccessor>().Push(new("client", id.ToString()));
    private static async Task<ContentJobInfo> Wait(McpWebApplicationFactory factory, Guid id)
    {
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        for (var i = 0; i < 150; i++)
        {
            await scheduler.DispatchAsync(default);
            var job = await jobs.GetJobAsync(id, default);
            if (job.Status is not ("queued" or "running")) return job;
            await Task.Delay(20);
        }
        throw new TimeoutException("内容任务未结束。");
    }

    [Fact]
    public async Task Upload_download_and_delete_are_authenticated_and_isolated()
    {
        await using var factory = Factory(new());
        var first = await Client(factory, "first"); using var a = first.Http;
        var second = await Client(factory, "second"); using var b = second.Http;
        using var anonymous = factory.CreateClient(); using var admin = Admin(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/files/unknown")).StatusCode);
        var file = await Upload(a);
        Assert.Equal("text/plain", file.MimeType);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/files/{file.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/files/{file.FileId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/files/{file.FileId}")).StatusCode);
        Assert.Equal("中文 hello", await a.GetStringAsync($"/api/files/{file.FileId}/download"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/files/{file.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/files/{file.FileId}")).StatusCode);
    }
    [Theory]
    [InlineData("fake.pdf")]
    [InlineData("fake.docx")]
    [InlineData("fake.png")]
    [InlineData("fake.exe")]
    public async Task Upload_rejects_disguised_or_unsupported_files(string name)
    {
        await using var factory = Factory(new()); using var admin = Admin(factory);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent("not a binary file"), "file", name);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/files", form)).StatusCode);
    }
    [Fact]
    public async Task Size_limits_are_enforced_and_partial_uploads_are_removed()
    {
        await using var factory = Factory(new()); using var admin = Admin(factory);
        (await admin.PutAsJsonAsync("/api/admin/settings", new { changes = new Dictionary<string, string> { ["Content:MaxFileBytes"] = "1024" } })).EnsureSuccessStatusCode();
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(new string('a', 2048)), "file", "large.txt");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await admin.PostAsync("/api/admin/files", form)).StatusCode);
        Assert.Empty(Directory.GetFiles(factory.DatabasePath + ".files"));
    }
    [Fact]
    public async Task Jobs_use_persisted_client_identity_and_results_support_bounded_reads()
    {
        var state = new EngineState(); await using var factory = Factory(state);
        var first = await Client(factory, "first"); using var http = first.Http;
        var second = await Client(factory, "second"); using var other = second.Http;
        var file = await Upload(http, "0123456789中文");
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        var files = factory.Services.GetRequiredService<IContentFileStore>();
        Guid id;
        using (As(factory, first.Id)) id = await jobs.SubmitJobAsync("content", "document", JsonSerializer.SerializeToElement(new { fileId = file.FileId }), default);
        // Dispatch under a different caller: execution must still use the persisted owner.
        using (As(factory, second.Id))
        {
            await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => jobs.GetJobAsync(id, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => jobs.CancelJobAsync(id, default));
        }
        using (As(factory, first.Id))
        {
            var result = await Wait(factory, id);
            Assert.Equal("succeeded", result.Status);
            Assert.Equal(first.Id.ToString(), state.Identity!.Id);
            var output = result.Result!.Value.GetProperty("files")[0].GetProperty("fileId").GetString()!;
            var page = await files.ReadTextAsync(output, 2, 4, default);
            Assert.Equal("2345", page.Content); Assert.Equal(6, page.NextStartIndex); Assert.Equal(12, page.TotalCharacters);
            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadTextAsync(output, 0, 20001, default));
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/files/{output}")).StatusCode);
        }
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
        var run = await db.TaskRuns.SingleAsync(r => r.Id == id);
        Assert.Null(run.TaskId); Assert.Equal("client", run.SubmitterKind); Assert.Equal(file.FileId, run.InputFileId);
        Assert.NotNull(run.InvocationLogId);
        Assert.Equal(first.Id, (await db.InvocationLogs.SingleAsync(l => l.Id == run.InvocationLogId)).ClientApplicationId);
    }
    [Fact]
    public async Task Queued_files_are_pinned_and_running_cancellation_reaches_provider()
    {
        var state = new EngineState { Block = true }; await using var factory = Factory(state);
        var owner = await Client(factory, "owner"); using var http = owner.Http;
        var file = await Upload(http); using var identity = As(factory, owner.Id);
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        var id = await jobs.SubmitJobAsync("content", "document", JsonSerializer.SerializeToElement(new { fileId = file.FileId }), default);
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/files/{file.FileId}")).StatusCode);
        await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await jobs.CancelJobAsync(id, default);
        await state.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("cancelled", (await Wait(factory, id)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/files/{file.FileId}")).StatusCode);
    }
    [Fact]
    public async Task Queue_cancellation_and_opt_in_are_enforced()
    {
        await using var factory = Factory(new()); using var admin = Admin(factory);
        var file = await Upload(admin, admin: true);
        using var identity = factory.Services.GetRequiredService<IPluginExecutionContextAccessor>().Push(new("admin", "administrator"));
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.SubmitJobAsync("content", "cleanup", JsonSerializer.SerializeToElement(new { }), default));
        var id = await jobs.SubmitJobAsync("content", "document", JsonSerializer.SerializeToElement(new { fileId = file.FileId }), default);
        Assert.Equal("cancelled", (await jobs.CancelJobAsync(id, default)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/files/{file.FileId}")).StatusCode);
    }
    [Fact]
    public async Task Mcp_tools_can_submit_and_read_a_file_job()
    {
        await using var factory = Factory(new());
        var owner = await Client(factory, "mcp"); using var http = owner.Http;
        var file = await Upload(http);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + owner.Key }
        }, http, ownsHttpClient: false));
        Assert.Equal(9, (await mcp.ListToolsAsync()).Count);
        var called = await mcp.CallToolAsync("kaung_document_parse", new Dictionary<string, object?> { ["fileId"] = file.FileId });
        Assert.NotEqual(true, called.IsError);
        var result = JsonDocument.Parse(called.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        var id = result.GetProperty("jobId").GetGuid();
        using (As(factory, owner.Id)) Assert.Equal("succeeded", (await Wait(factory, id)).Status);
        var read = await mcp.CallToolAsync("kaung_content_job_get", new Dictionary<string, object?> { ["jobId"] = id.ToString() });
        Assert.Contains("succeeded", JsonSerializer.Serialize(read));
        var forbiddenUrl = await mcp.CallToolAsync("kaung_file_import", new Dictionary<string, object?> { ["url"] = "http://127.0.0.1/internal.txt" });
        Assert.True(forbiddenUrl.IsError);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
        var submittedLog = await db.InvocationLogs.SingleAsync(l => l.ToolName == "kaung_document_parse" && l.ClientApplicationId == owner.Id);
        Assert.Equal((await db.TaskRuns.SingleAsync(r => r.Id == id)).CorrelationId, submittedLog.CorrelationId);
    }
    [Fact]
    public async Task Expiry_skips_pinned_files_then_removes_inputs_and_outputs()
    {
        await using var factory = Factory(new()); using var admin = Admin(factory);
        var file = await Upload(admin, admin: true);
        using var identity = factory.Services.GetRequiredService<IPluginExecutionContextAccessor>().Push(new("admin", "administrator"));
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        var files = factory.Services.GetRequiredService<IContentFileStore>();
        var id = await jobs.SubmitJobAsync("content", "document", JsonSerializer.SerializeToElement(new { fileId = file.FileId }), default);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
        await db.ContentFiles.ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ExpiresTicks, DateTimeOffset.UtcNow.AddDays(-1).UtcTicks));
        Assert.Equal(0, await files.CleanupAsync(default));
        Assert.Equal("succeeded", (await Wait(factory, id)).Status);
        await db.ContentFiles.ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ExpiresTicks, DateTimeOffset.UtcNow.AddDays(-1).UtcTicks));
        Assert.Equal(2, await files.CleanupAsync(default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => files.GetAsync(file.FileId, default));
    }
    [Fact]
    public async Task Restart_preserves_queue_and_marks_running_jobs_interrupted()
    {
        await using var factory = Factory(new());
        var owner = await Client(factory, "restart"); using var http = owner.Http;
        var file = await Upload(http); using var identity = As(factory, owner.Id);
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        var arguments = JsonSerializer.SerializeToElement(new { fileId = file.FileId });
        var queued = await jobs.SubmitJobAsync("content", "document", arguments, default);
        var interrupted = await jobs.SubmitJobAsync("content", "document", arguments, default);
        var database = factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
        await using (var db = await database.CreateDbContextAsync())
            await db.TaskRuns.Where(r => r.Id == interrupted).ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, "running"));
        var restarted = new SchedulerService(database, factory.Services, factory.Services.GetRequiredService<PluginRuntime>(),
            factory.Services.GetRequiredService<TimeProvider>(), factory.Services.GetRequiredService<IConfiguration>(), NullLogger<SchedulerService>.Instance);
        await restarted.InitializeAsync(default);
        Assert.Equal("interrupted", (await jobs.GetJobAsync(interrupted, default)).Status);
        await restarted.DispatchAsync(default);
        Assert.Equal("succeeded", (await Wait(factory, queued)).Status);
    }
    [Fact]
    public async Task Disabling_content_cancels_one_off_queue_and_keeps_running_job()
    {
        var state = new EngineState { Block = true }; await using var factory = Factory(state);
        var owner = await Client(factory, "disable"); using var http = owner.Http; using var admin = Admin(factory);
        var file = await Upload(http); using var identity = As(factory, owner.Id);
        var jobs = factory.Services.GetRequiredService<IPluginJobService>();
        var arguments = JsonSerializer.SerializeToElement(new { fileId = file.FileId });
        var running = await jobs.SubmitJobAsync("content", "document", arguments, default);
        await factory.Services.GetRequiredService<SchedulerService>().DispatchAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = await jobs.SubmitJobAsync("content", "document", arguments, default);
        (await admin.PutAsJsonAsync("/api/admin/plugins/content/enabled", new { isEnabled = false })).EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await jobs.GetJobAsync(queued, default)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.SubmitJobAsync("content", "document", arguments, default));
        state.Release.TrySetResult();
        Assert.Equal("succeeded", (await Wait(factory, running)).Status);
    }
    [Fact]
    public async Task Text_paging_does_not_split_unicode_surrogate_pairs()
    {
        await using var factory = Factory(new()); using var admin = Admin(factory);
        using var identity = factory.Services.GetRequiredService<IPluginExecutionContextAccessor>().Push(new("admin", "administrator"));
        var files = factory.Services.GetRequiredService<IContentFileStore>();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("a😀b"));
        var file = await files.CreateArtifactAsync("content", "unicode.txt", "text/plain", input, default);
        var first = await files.ReadTextAsync(file.FileId, 0, 2, default);
        Assert.Equal("a", first.Content); Assert.Equal(1, first.NextStartIndex);
        var second = await files.ReadTextAsync(file.FileId, first.NextStartIndex!.Value, 2, default);
        Assert.Equal("😀", second.Content); Assert.Equal(3, second.NextStartIndex);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadTextAsync(file.FileId, 2, 2, default));
    }
    [Fact]
    public async Task Migration_preserves_existing_clients_and_scheduled_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaung-content-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new KaungMcpDbContext(new DbContextOptionsBuilder<KaungMcpDbContext>().UseSqlite("Data Source=" + path).Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261012022152_InvocationOwnership");
            var plan = new ScheduledTask { Name = "existing", PluginId = "diagnostics", TargetId = "old", CreatedTicks = 1 };
            db.ScheduledTasks.Add(plan);
            db.ClientApplications.Add(new() { Id = Guid.NewGuid(), Name = "existing-client", KeyId = "old-key",
                KeyHash = new byte[32], IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var runId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO TaskRuns (Id,TaskId,PluginId,PluginVersion,TargetType,TargetId,DefinitionVersion,
                    ArgumentsJson,Trigger,CreatedTicks,Attempt,TimeoutSeconds,MaxRetries,RiskConfirmed,CancelRequested,CorrelationId,Status)
                VALUES ({{runId}},{{plan.Id}},'diagnostics','1.0.0','handler','old',1,'{}','manual',1,1,300,0,0,0,'old-correlation','succeeded')
                """);
            await migrator.MigrateAsync();
            Assert.Equal("existing", (await db.ScheduledTasks.SingleAsync()).Name);
            Assert.Equal(plan.Id, (await db.TaskRuns.SingleAsync()).TaskId);
            Assert.Empty(await db.ContentFiles.ToArrayAsync());
            Assert.Equal("existing-client", (await db.ClientApplications.SingleAsync()).Name);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
}
