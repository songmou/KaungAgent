using KaungMcp.Plugin;
using KaungMcp.Plugins;
using KaungMcp.Web;
namespace KaungMcp.Capabilities;

public sealed record OwnedSetting(string OwnerId, string OwnerKind, string OwnerName, PluginSettingDefinition Definition);
public sealed class SettingsDefinitionService(PluginRuntime plugins)
{
    private static PluginSettingDefinition Field(string key, string name, string group, string groupName,
        string type = "text", bool runtime = false, string? unit = null, decimal? min = null, decimal? max = null, string? description = null,
        IReadOnlyList<PluginSettingOption>? options = null) => new(key, name, "", runtime)
        { GroupId = group, GroupName = groupName, Type = type, Unit = unit, Minimum = min, Maximum = max, Description = description, Options = options };
    public IReadOnlyList<OwnedSetting> Definitions()
    {
        List<OwnedSetting> result = [];
        void Add(string owner, string kind, string name, PluginSettingDefinition field) => result.Add(new(owner, kind, name, field));
        foreach (var field in WebSettings.Definitions)
        {
            var search = field.Key.StartsWith("WebSearch:");
            var boolean = field.Key.EndsWith(":Enabled") || field.Key.EndsWith("FallbackEnabled");
            var numeric = field.Key.EndsWith("Seconds") || field.Key.EndsWith("Bytes");
            Add("web", "module", "网页能力", field with { GroupId = search ? "search" : "fetch", GroupName = search ? "搜索" : "网页读取",
                Type = boolean ? "bool" : numeric ? "number" : field.Key.EndsWith("Url") ? "url" : "text",
                Unit = field.Key.EndsWith("Seconds") ? "秒" : field.Key.EndsWith("Bytes") ? "字节" : null,
                Minimum = numeric ? field.Key.EndsWith("Bytes") ? 65536 : 1 : null,
                Maximum = numeric ? field.Key.EndsWith("Bytes") ? search ? 5242880 : 10485760 : 60 : null,
                Description = field.Key.Contains("StaticFallback") ? "主搜索不可用时使用静态搜索回退。" : field.Key.Contains("BrowserFallback") ? "静态读取未获得正文时尝试浏览器，需要安装 Chromium。" : "保存后作用于后续请求。" });
        }
        Add("web", "module", "网页能力", Field("SearXngLauncher:Mode", "SearXNG 启动方式", "dependencies", "依赖服务", "select", options: [new("local", "本地容器"), new("external", "外部服务"), new("disabled", "不启动")], description: "重启一键启动脚本后生效；搜索接口开关独立控制。"));
        Add("web", "module", "网页能力", Field("SearXngLauncher:Port", "本地 SearXNG 端口", "dependencies", "依赖服务", "number", min: 1, max: 65535));
        Add("web", "module", "网页能力", Field("SearXngLauncher:ExternalEndpoint", "外部 SearXNG 地址", "dependencies", "依赖服务", "url"));
        foreach (var f in new[] {
            Field("McpHost:LocalPort", "本机 HTTP 端口", "listener", "服务监听", "number", min:1, max:65535),
            Field("McpHost:LanEnabled", "启用局域网 HTTPS", "listener", "服务监听", "bool"),
            Field("McpHost:LanAddress", "局域网私有 IPv4 地址", "listener", "服务监听"),
            Field("McpHost:LanPort", "局域网 HTTPS 端口", "listener", "服务监听", "number", min:1, max:65535),
            Field("AllowedHosts", "允许的 Host", "security", "安全", description:"多个主机以分号分隔。"),
            Field("ConnectionStrings:KaungMcp", "SQLite 连接字符串", "database", "数据库", description:"仅接受已有且结构兼容的数据库；不会复制原库数据。"),
            Field("Logging:LogLevel:Default", "默认日志级别", "logging", "日志", "select", options: Levels()),
            Field("Logging:LogLevel:Microsoft.AspNetCore", "ASP.NET Core 日志级别", "logging", "日志", "select", options: Levels()),
            Field("WebFetch:TimeoutSeconds", "公共网络请求超时", "network", "公共网络", "number", true, "秒", 1, 60, "影响网页读取及珠宝采集的网络请求。"),
            Field("WebFetch:MaxRedirects", "最大重定向次数", "network", "公共网络", "number", true, "次", 0, 10, "影响所有使用公共网络服务的插件。"),
            Field("WebFetch:UserAgent", "网络请求 User-Agent", "network", "公共网络", runtime:true, description:"影响网页读取及珠宝采集的网络请求。"),
            Field("WebFetch:RobotsCacheMinutes", "robots 缓存时长", "network", "公共网络", "number", true, "分钟", 1, 1440, "影响网页读取及珠宝采集的访问规则检查。") }) Add("host", "host", "系统设置", f);
        foreach (var p in plugins.Plugins.Where(p => p.Registration is not null))
            foreach (var f in p.Registration!.ConfigurationProviders.SelectMany(c => c.Settings)) Add(p.Manifest.Id, "plugin", p.Manifest.Name, f);
        return result;
    }
    private static PluginSettingOption[] Levels() => new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }.Select(x => new PluginSettingOption(x, x)).ToArray();
}
