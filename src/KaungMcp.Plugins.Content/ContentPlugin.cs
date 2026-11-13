using System.Text.Json;
using KaungMcp.Content;
using KaungMcp.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace KaungMcp.Plugins.Content;

public sealed class ContentPlugin : IKaungMcpPlugin
{
    public void Register(PluginRegistrationContext context)
    {
        // Plugin registrations are staged in a separate collection. Configure the host's
        // factory without duplicating its framework-wide singleton registrations.
        context.Services.Configure<HttpClientFactoryOptions>("ContentEngine", options =>
        {
            options.HttpClientActions.Add(client => client.Timeout = Timeout.InfiniteTimeSpan);
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            });
        });
        context.Services.AddSingleton<ContentEngineClient>();
        context.Services.AddSingleton<IOcrProvider>(s => s.GetRequiredService<ContentEngineClient>());
        context.Services.AddSingleton<IDocumentParser>(s => s.GetRequiredService<ContentEngineClient>());
        context.Services.AddSingleton<ISpeechTranscriber>(s => s.GetRequiredService<ContentEngineClient>());
        context.Services.AddTransient<OcrHandler>();
        context.Services.AddTransient<DocumentHandler>();
        context.Services.AddTransient<SpeechHandler>();
        context.Services.AddTransient<CleanupHandler>();
        context.Services.AddTransient<OcrHealth>();
        context.Services.AddTransient<DocumentHealth>();
        context.Services.AddTransient<SpeechHealth>();
        context.CapabilityAssemblies.Add(GetType().Assembly);
        foreach (var name in new[] { "kaung_file_import", "kaung_file_info", "kaung_file_delete", "kaung_ocr_recognize",
            "kaung_document_parse", "kaung_audio_transcribe", "kaung_content_job_get", "kaung_content_job_cancel", "kaung_content_read" })
            context.Tools.Add(new(name));
        context.Tasks.Add(new("ocr", "OCR 识别", typeof(OcrHandler), Schema("ocr"), DefaultTimeoutSeconds: 7200) { AllowOneOff = true });
        context.Tasks.Add(new("document", "文档解析", typeof(DocumentHandler), Schema("document"), DefaultTimeoutSeconds: 7200) { AllowOneOff = true });
        context.Tasks.Add(new("speech", "音频转写", typeof(SpeechHandler), Schema("speech"), DefaultTimeoutSeconds: 7200) { AllowOneOff = true });
        context.Tasks.Add(new("cleanup", "过期文件清理", typeof(CleanupHandler), JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { }, additionalProperties = false
        }), Idempotent: true));
        context.DefaultTasks.Add(new("cleanup", "每日清理过期文件", "cleanup", JsonSerializer.SerializeToElement(new { }), 1440));
        context.ConfigurationProviders.Add(new ContentConfiguration());
        context.HealthChecks.AddRange([typeof(OcrHealth), typeof(DocumentHealth), typeof(SpeechHealth)]);
    }
    public Task InitializeAsync(PluginInitializationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    private static JsonElement Schema(string operation)
    {
        var properties = new Dictionary<string, object>
        {
            ["fileId"] = new { type = "string", minLength = 1, maxLength = 64 }
        };
        if (operation != "speech") properties["pages"] = new { type = "string", maxLength = 2000 };
        if (operation == "document")
        {
            properties["ocrMode"] = new { type = "string", @enum = new[] { "auto", "force", "off" } };
            properties["sheetNames"] = new { type = "array", items = new { type = "string", maxLength = 128 }, maxItems = 100, uniqueItems = true };
            properties["encoding"] = new { type = "string", @enum = new[] { "auto", "utf-8", "utf-16", "gb18030" } };
        }
        if (operation == "speech")
        {
            properties["language"] = new { type = "string", @enum = new[] { "auto", "zh", "en" } };
            properties["wordTimestamps"] = new { type = "boolean" };
        }
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required = new[] { "fileId" }, additionalProperties = false });
    }
}

public sealed class ContentConfiguration : IPluginConfigurationProvider
{
    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new("Content:EngineEndpoint", "本地引擎地址", "http://127.0.0.1:8092", true)
        { GroupId = "engine", GroupName = "处理引擎", Type = "url", Description = "可信内部引擎地址；凭据通过 Content__EngineKey 环境变量配置。" },
        Number("MaxFileBytes", "单文件上限", 104857600, 1024, 1073741824, "字节"),
        Number("MaxPdfPages", "PDF 页数上限", 200, 1, 2000, "页"),
        Number("MaxAudioMinutes", "音频时长上限", 120, 1, 1440, "分钟"),
        Number("JobTimeoutSeconds", "任务执行超时", 7200, 1, 86400, "秒"),
        Number("RetentionDays", "文件与结果保留", 7, 1, 365, "天")
    ];
    private static PluginSettingDefinition Number(string key, string name, int value, int min, int max, string unit) =>
        new("Content:" + key, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), true)
        { GroupId = "limits", GroupName = "处理限制", Type = "number", Minimum = min, Maximum = max, Unit = unit };
    public void Validate(IReadOnlyDictionary<string, string> values)
    {
        if (!Uri.TryCreate(values["Content:EngineEndpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("引擎地址必须是无凭据、查询或片段的 HTTP(S) 地址。");
        foreach (var setting in Settings.Where(s => s.Type == "number"))
            if (!int.TryParse(values[setting.Key], out var n) || n < setting.Minimum || n > setting.Maximum)
                throw new ArgumentException(setting.Name + "超出允许范围。");
    }
}
