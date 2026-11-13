using System.Text.Json;
using System.Text.RegularExpressions;
using KaungMcp.Plugin;

namespace KaungMcp.Plugins.Content;

public abstract class ContentHandler(IContentFileStore files, string[] extensions) : IPluginTaskHandler
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken cancellationToken)
    {
        if (context.ScheduleType != "once") throw new ArgumentException("文件识别与解析使用一次性任务，请通过内容工具提交。");
        var file = await files.GetAsync(arguments.GetProperty("fileId").GetString()!, cancellationToken);
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (file.IsArtifact || !extensions.Contains(extension)) throw new ArgumentException("该操作不支持此文件类型。");
        if (arguments.TryGetProperty("pages", out var pageValue) && pageValue.GetString() is { Length: > 0 } pages)
        {
            if (extension != ".pdf") throw new ArgumentException("页范围只适用于 PDF。");
            if (!Regex.IsMatch(pages, @"^[1-9]\d*(?:-[1-9]\d*)?(?:,[1-9]\d*(?:-[1-9]\d*)?)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ArgumentException("页范围格式应为 1-3,5。");
            foreach (var range in pages.Split(','))
            {
                var bounds = range.Split('-');
                if (!int.TryParse(bounds[0], out var begin) || !int.TryParse(bounds[^1], out var end) || begin > end || end > 2000)
                    throw new ArgumentException("页范围无效或超过最大页数。");
            }
        }
        if (arguments.TryGetProperty("sheetNames", out var names) && names.GetArrayLength() > 0 && extension is not (".xls" or ".xlsx"))
            throw new ArgumentException("工作表选择只适用于 Excel。");
    }
    public IReadOnlyList<string> GetConcurrencyKeys(JsonElement arguments) => ["content/engine"];
    protected abstract Task<ContentProcessingResult> ProcessAsync(Guid runId, JsonElement arguments, CancellationToken ct);
    public async Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await ProcessAsync(context.RunId, arguments, cancellationToken);
        var primary = result.Files.FirstOrDefault();
        return new(true, result.Summary, JsonSerializer.SerializeToElement(result, Json),
            primary is null ? null : new(primary.FileId, primary.MimeType, primary.Name));
    }
}
public sealed class OcrHandler(IContentFileStore files, IOcrProvider provider) : ContentHandler(files,
    [".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"])
{
    protected override Task<ContentProcessingResult> ProcessAsync(Guid id, JsonElement args, CancellationToken ct) => provider.RecognizeAsync(id, args, ct);
}
public sealed class DocumentHandler(IContentFileStore files, IDocumentParser provider) : ContentHandler(files,
    [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".md", ".csv"])
{
    protected override Task<ContentProcessingResult> ProcessAsync(Guid id, JsonElement args, CancellationToken ct) => provider.ParseAsync(id, args, ct);
}
public sealed class SpeechHandler(IContentFileStore files, ISpeechTranscriber provider) : ContentHandler(files,
    [".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac"])
{
    protected override Task<ContentProcessingResult> ProcessAsync(Guid id, JsonElement args, CancellationToken ct) => provider.TranscribeAsync(id, args, ct);
}
public sealed class CleanupHandler(IContentFileStore files) : IPluginTaskHandler
{
    public Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var count = await files.CleanupAsync(cancellationToken);
        return new(true, $"已清理 {count} 个过期文件。");
    }
}
