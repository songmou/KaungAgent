using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Configuration;
using KaungMcp.Content;
using KaungMcp.Plugin;
using Microsoft.Extensions.Configuration;

namespace KaungMcp.Plugins.Content;

public sealed record ContentProcessingResult(string Summary, IReadOnlyList<ContentFileInfo> Files, IReadOnlyList<string> Warnings);
public interface IOcrProvider
{
    Task<ContentProcessingResult> RecognizeAsync(Guid runId, JsonElement arguments, CancellationToken ct);
}
public interface IDocumentParser
{
    Task<ContentProcessingResult> ParseAsync(Guid runId, JsonElement arguments, CancellationToken ct);
}
public interface ISpeechTranscriber
{
    Task<ContentProcessingResult> TranscribeAsync(Guid runId, JsonElement arguments, CancellationToken ct);
}

public sealed class ContentEngineClient(IHttpClientFactory clients, IContentFileStore files,
    ManagedSettingsStore settings, IConfiguration configuration) : IOcrProvider, IDocumentParser, ISpeechTranscriber
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var key = configuration["Content:EngineKey"] ?? configuration["CONTENT_ENGINE_KEY"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) throw new ArgumentException("未配置内容引擎凭据 Content__EngineKey（至少 32 字符）。");
        var endpoint = settings.GetOptions<ContentOptions>("Content").EngineEndpoint.TrimEnd('/');
        var request = new HttpRequestMessage(method, endpoint + path);
        request.Headers.Add("X-Engine-Key", key);
        return request;
    }
    public Task<ContentProcessingResult> RecognizeAsync(Guid id, JsonElement args, CancellationToken ct) => ProcessAsync("ocr", id, args, ct);
    public Task<ContentProcessingResult> ParseAsync(Guid id, JsonElement args, CancellationToken ct) => ProcessAsync("document", id, args, ct);
    public Task<ContentProcessingResult> TranscribeAsync(Guid id, JsonElement args, CancellationToken ct) => ProcessAsync("speech", id, args, ct);

    private async Task<ContentProcessingResult> ProcessAsync(string operation, Guid id, JsonElement arguments, CancellationToken ct)
    {
        var fileId = arguments.GetProperty("fileId").GetString()!;
        var info = await files.GetAsync(fileId, ct);
        var options = settings.GetOptions<ContentOptions>("Content");
        using var request = Request(HttpMethod.Post, $"/v1/process/{operation}/{id}");
        using var multipart = new MultipartFormDataContent();
        await using var source = await files.OpenReadAsync(fileId, ct);
        var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue(info.MimeType);
        multipart.Add(content, "file", info.Name);
        multipart.Add(new StringContent(JsonSerializer.Serialize(new
        {
            arguments,
            limits = new { maxFileBytes = options.MaxFileBytes, maxPdfPages = options.MaxPdfPages,
                maxAudioMinutes = options.MaxAudioMinutes, timeoutSeconds = options.JobTimeoutSeconds }
        }, Json)), "options");
        request.Content = multipart;
        var created = new List<ContentFileInfo>();
        try
        {
            using var response = await clients.CreateClient("ContentEngine").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Return only bounded, explicitly public errors from the internal engine.
                EngineError? error = null;
                try
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(ct);
                    using var errorBuffer = new MemoryStream();
                    await CopyBoundedAsync(errorStream, errorBuffer, 16384, ct);
                    error = JsonSerializer.Deserialize<EngineError>(errorBuffer.ToArray(), Json);
                }
                catch (Exception exception) when (exception is JsonException or ContentLimitException) { }
                throw new ArgumentException(error?.Detail ?? $"内容引擎处理失败：HTTP {(int)response.StatusCode}。");
            }
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var archiveBuffer = new MemoryStream();
            await CopyBoundedAsync(body, archiveBuffer, options.MaxFileBytes, ct);
            archiveBuffer.Position = 0;
            using var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > 256 || archive.Entries.Sum(e => e.Length) > options.MaxFileBytes)
                throw new ContentLimitException("引擎结果超过限制。");
            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new ArgumentException("引擎未返回产物清单。");
            if (manifestEntry.Length > 65536) throw new ArgumentException("引擎产物清单过大。");
            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<EngineManifest>(manifestStream, Json, ct)
                ?? throw new ArgumentException("引擎产物清单无效。");
            if (manifest.Files.Length is < 1 or > 255 || manifest.Files.Select(f => f.Name).Distinct().Count() != manifest.Files.Length)
                throw new ArgumentException("引擎产物清单无效。");
            foreach (var artifact in manifest.Files)
            {
                if (Path.GetFileName(artifact.Name) != artifact.Name || artifact.Name.Contains('\\'))
                    throw new ArgumentException("引擎产物名称无效。");
                var entry = archive.GetEntry(artifact.Name) ?? throw new ArgumentException("引擎产物缺失。");
                await using var input = entry.Open();
                created.Add(await files.CreateArtifactAsync("content", artifact.Name, artifact.MimeType, input, ct));
            }
            ct.ThrowIfCancellationRequested();
            return new(manifest.Summary, created, manifest.Warnings);
        }
        catch
        {
            // A disconnected HTTP request must not leave an expensive model process running.
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var cancel = Request(HttpMethod.Delete, $"/v1/executions/{id}");
                using var response = await clients.CreateClient("ContentEngine").SendAsync(cancel, stop.Token);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            foreach (var file in created) await files.DeleteAsync(file.FileId, CancellationToken.None);
            throw;
        }
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > maximum) throw new ContentLimitException("引擎结果超过限制。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    public async Task<PluginHealth> HealthAsync(string capability, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = Request(HttpMethod.Get, "/v1/health/" + capability);
            using var response = await clients.CreateClient("ContentEngine").SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var health = await response.Content.ReadFromJsonAsync<EngineHealth>(Json, timeout.Token);
            return new(health?.Ready == true ? "healthy" : "degraded", capability + ": " + (health?.Detail ?? "引擎未就绪"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException or JsonException)
        { return new("degraded", capability + ": 引擎不可用或未配置，请检查内部引擎、模型和凭据。"); }
    }
    private sealed record EngineError(string Detail);
    private sealed record EngineHealth(bool Ready, string Detail);
    private sealed record EngineArtifact(string Name, string MimeType);
    private sealed record EngineManifest(string Summary, EngineArtifact[] Files, string[] Warnings);
}

public sealed class OcrHealth(ContentEngineClient engine) : IPluginHealthCheck
{
    public Task<PluginHealth> CheckAsync(CancellationToken cancellationToken) => engine.HealthAsync("ocr", cancellationToken);
}
public sealed class DocumentHealth(ContentEngineClient engine) : IPluginHealthCheck
{
    public Task<PluginHealth> CheckAsync(CancellationToken cancellationToken) => engine.HealthAsync("document", cancellationToken);
}
public sealed class SpeechHealth(ContentEngineClient engine) : IPluginHealthCheck
{
    public Task<PluginHealth> CheckAsync(CancellationToken cancellationToken) => engine.HealthAsync("speech", cancellationToken);
}
