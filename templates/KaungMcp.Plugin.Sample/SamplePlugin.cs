using System.ComponentModel;
using System.Text.Json;
using KaungMcp.Configuration;
using KaungMcp.Plugin;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace KaungMcp.Sample;

public sealed class SamplePlugin : IKaungMcpPlugin
{
    public void Register(PluginRegistrationContext context)
    {
        context.Services.AddSingleton(new ReportStorage(Path.Combine(context.DataDirectory, "artifacts")));
        context.Services.AddTransient<ReportHandler>();
        context.CapabilityAssemblies.Add(GetType().Assembly);
        context.Tools.Add(new("kaung_sample_echo", Schedulable: true));
        context.ConfigurationProviders.Add(new SampleConfiguration());
        context.Tasks.Add(new("write_report", "生成示例报告", typeof(ReportHandler), JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { text = new { type = "string", minLength = 1, maxLength = 10000 } },
            required = new[] { "text" },
            additionalProperties = false
        }), Idempotent: true));
        context.DefaultTasks.Add(new("report", "示例报告计划", "write_report",
            JsonSerializer.SerializeToElement(new { text = "示例报告内容" }), 60, IsEnabled: false));
    }

    public Task InitializeAsync(PluginInitializationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record ReportStorage(string Directory);

public sealed class ReportHandler(ReportStorage storage) : IPluginTaskHandler
{
    public Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public IReadOnlyList<string> GetConcurrencyKeys(JsonElement arguments) => ["sample/report"];

    public async Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var name = context.RunId.ToString("N") + ".txt";
        await File.WriteAllTextAsync(Path.Combine(storage.Directory, name), arguments.GetProperty("text").GetString(), cancellationToken);
        return new(true, "示例报告已保存。", Artifact: new(name, "text/plain", name));
    }
}

[McpServerToolType]
public sealed class SampleTools(ManagedSettingsStore settings)
{
    [McpServerTool(Name = "kaung_sample_echo", ReadOnly = true, Idempotent = true)]
    [Description("返回附带插件配置前缀的文本。")]
    public string Echo(string message) => settings.GetOptions<SampleOptions>("Sample").Prefix + message;
}

public sealed class SampleOptions
{
    public string Prefix { get; set; } = "Sample: ";
}

public sealed class SampleConfiguration : IPluginConfigurationProvider
{
    public IReadOnlyList<PluginSettingDefinition> Settings { get; } = [new("Sample:Prefix", "文本前缀", "Sample: ", Runtime: true) { GroupId = "output", GroupName = "输出设置", Description = "工具输出文本的前缀，保存后即时生效。" }];
    public void Validate(IReadOnlyDictionary<string, string> values)
    {
        if (values["Sample:Prefix"].Length > 100) throw new ArgumentException("文本前缀最多 100 个字符。");
    }
}

[McpServerResourceType]
public sealed class SampleResources
{
    [McpServerResource(UriTemplate = "kaung://sample/items/{id}")]
    [Description("演示带参数的插件资源。")]
    public string Item(string id) => "Item: " + id;
}
