using System.Text.Json;
using Cronos;
using KaungMcp.Plugin;
using KaungMcp.Jewelry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Plugins.Jewelry;

public sealed class JewelryPlugin : IKaungMcpPlugin
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public void Register(PluginRegistrationContext context)
    {
        context.Services.AddOptions<JewelryOptions>().Bind(context.Configuration.GetSection("Jewelry"));
        context.Services.AddDbContextFactory<JewelryDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(context.DataDirectory, "plugin.db")};Default Timeout=30"));
        context.Services.AddSingleton<IJewelryAdapter, JewelryAdapter>();
        context.Services.AddSingleton<JewelryService>();
        context.Services.AddTransient<MarketCollectionHandler>();
        context.Services.AddTransient<BrandCollectionHandler>();
        context.Services.AddTransient<JewelryHealth>();
        context.HealthChecks.Add(typeof(JewelryHealth));
        context.CapabilityAssemblies.Add(GetType().Assembly);
        context.Tools.AddRange(new[] { "list_instruments", "get_market_quotes", "get_brand_quotes", "get_price_history", "compare_prices", "convert_price" }
            .Select(id => new PluginToolPolicy("kaung_jewelry_" + id, true, SanitizeErrors: true)));
        context.Tools.Add(new("kaung_jewelry_get_source_status", false, false));
        context.Tasks.Add(new("collect_market", "采集市场行情", typeof(MarketCollectionHandler),
            Json(new { type = "object", properties = new { sourceIds = new { type = "array", items = new { type = "string", @enum = new[] { "gold_api", "sge_daily", "sge_benchmark" } }, minItems = 1, uniqueItems = true } }, required = new[] { "sourceIds" }, additionalProperties = false }), Idempotent: true));
        context.Tasks.Add(new("collect_brands", "采集品牌报价", typeof(BrandCollectionHandler),
            Json(new { type = "object", properties = new { }, additionalProperties = false }), Idempotent: true));
        context.DefaultTasks.AddRange([
            new("international", "国际金价采集", "collect_market", Json(new { sourceIds = new[] { "gold_api" } }), 15),
            new("shanghai", "上海行情采集", "collect_market", Json(new { sourceIds = new[] { "sge_daily", "sge_benchmark" } }), 15),
            new("brands", "品牌报价采集", "collect_brands", Json(new { }), 60)]);
        context.ConfigurationProviders.Add(new JewelryConfiguration());
    }

    public async Task InitializeAsync(PluginInitializationContext context, CancellationToken ct)
    {
        Directory.CreateDirectory(context.DataDirectory);
        await using var db = await context.Services.GetRequiredService<IDbContextFactory<JewelryDbContext>>().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await context.Services.GetRequiredService<JewelryService>().InitializeAsync(ct);
    }
}

public sealed class JewelryHealth(IDbContextFactory<JewelryDbContext> database) : IPluginHealthCheck
{
    public async Task<PluginHealth> CheckAsync(CancellationToken ct)
    {
        await using var db = await database.CreateDbContextAsync(ct);
        return new(await db.Database.CanConnectAsync(ct) ? "ready" : "degraded");
    }
}

public sealed class JewelryConfiguration : IPluginConfigurationProvider
{
    public IReadOnlyList<PluginSettingDefinition> Settings { get; } = [
        new("Jewelry:TimeoutSeconds", "采集超时", "20") { Type = "number", Unit = "秒", Minimum = 1, Maximum = 60, Description = "单次采集超时；修改后重启服务。" },
        new("Jewelry:Enabled", "启用行情采集", "true") { Type = "bool", Description = "控制业务采集；定时计划仍需单独启用。" },
        ..JewelryCatalog.Sources.Select(s => new PluginSettingDefinition("Jewelry:Sources:" + s.Id, s.Name + "来源启用", (s.AdapterAvailable && s.Verification == "verified_sample").ToString().ToLowerInvariant())
        { Type = "bool", GroupId = s.Id is "sge_daily" or "sge_benchmark" or "gold_api" or "cme" ? "market-sources" : "brand-sources",
          GroupName = s.Id is "sge_daily" or "sge_benchmark" or "gold_api" or "cme" ? "市场行情来源" : "品牌报价来源", Description = s.Name + "的数据来源开关。",
          ReadOnlyReason = !s.AdapterAvailable || s.Verification != "verified_sample" ? "该来源尚未验证，不能启用。" : null })
    ];
    public void Validate(IReadOnlyDictionary<string, string> values)
    {
        if (!int.TryParse(values["Jewelry:TimeoutSeconds"], out var timeout) || timeout is < 1 or > 60)
            throw new ArgumentException("采集超时必须为 1..60 秒。");
        if (!bool.TryParse(values["Jewelry:Enabled"], out _)) throw new ArgumentException("采集开关必须为 true 或 false。");
        foreach (var source in JewelryCatalog.Sources)
        {
            if (!bool.TryParse(values["Jewelry:Sources:" + source.Id], out var enabled)) throw new ArgumentException("来源开关必须为 true 或 false。");
            if (enabled && (!source.AdapterAvailable || source.Verification != "verified_sample")) throw new ArgumentException("来源尚未验证，不能启用：" + source.Id);
        }
    }
}

public abstract class CollectionHandler(JewelryService service) : IPluginTaskHandler
{
    protected JewelryService Service { get; } = service;
    protected abstract string HandlerId { get; }
    protected abstract string[] Sources(JsonElement arguments);
    public IReadOnlyList<string> GetConcurrencyKeys(JsonElement arguments) => Sources(arguments).Select(id => "jewelry/source/" + id).ToArray();

    public Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken ct)
    {
        var ids = Sources(arguments);
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length) throw new ArgumentException("至少选择一个不重复的来源。");
        if (!ids.Any(id => Service.Enabled(JewelryCatalog.Sources.Single(s => s.Id == id)))) throw new ArgumentException("没有可用且已启用的采集来源。");
        foreach (var id in ids)
        {
            var source = JewelryCatalog.Sources.SingleOrDefault(s => s.Id == id) ?? throw new ArgumentException("未知来源。");
            if (!Service.Enabled(source) && HandlerId != "collect_brands") throw new ArgumentException("来源未启用或未验证。");
            if (!Service.Enabled(source)) continue;
            if (context.ScheduleType == "interval" && context.IntervalMinutes < source.MinimumIntervalMinutes)
                throw new ArgumentException($"来源 {id} 的周期不能短于 {source.MinimumIntervalMinutes} 分钟。");
            if (context.ScheduleType == "cron")
            {
                var expression = CronExpression.Parse(context.CronExpression!);
                var time = DateTime.UtcNow;
                for (var i = 0; i < 100; i++)
                {
                    var next = expression.GetNextOccurrence(time, TimeZoneInfo.FindSystemTimeZoneById(context.TimeZoneId))!.Value;
                    if (i > 0 && next - time < TimeSpan.FromMinutes(source.MinimumIntervalMinutes)) throw new ArgumentException("采集 Cron 过于频繁。");
                    time = next;
                }
            }
        }
        if (context.IsEnabled && context.Plans.Where(p => p.Id != context.PlanId && p.IsEnabled && p.TargetType == "handler" && p.TargetId is "collect_market" or "collect_brands")
            .Any(p => (p.TargetId == "collect_brands" ? JewelryBrands.CollectionSources :
                p.Arguments.GetProperty("sourceIds").EnumerateArray().Select(x => x.GetString()!).ToArray()).Intersect(ids).Any()))
            throw new ArgumentException("启用计划之间不能重复采集相同来源。");
        return Task.CompletedTask;
    }

    public async Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken ct)
    {
        var results = HandlerId == "collect_brands" ? await Service.CollectBrandsAsync(context.Trigger == "manual", ct)
            : await Service.CollectScheduledAsync(Sources(arguments), context.Trigger == "manual", ct);
        var errors = results.Where(x => x.Status == "failed").Select(x => x.Error).ToArray();
        return new(errors.Length == 0, $"成功 {results.Count(x => x.Status == "succeeded")}，失败 {errors.Length}",
            JsonSerializer.SerializeToElement(results), Error: errors.Length == 0 ? null : string.Join("; ", errors));
    }
}

public sealed class MarketCollectionHandler(JewelryService service) : CollectionHandler(service)
{
    protected override string HandlerId => "collect_market";
    protected override string[] Sources(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("请选择市场来源。");
        var selected = ids.EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (selected.Any(id => id is not ("gold_api" or "sge_daily" or "sge_benchmark"))) throw new ArgumentException("只能选择市场来源。");
        return selected;
    }
}

public sealed class BrandCollectionHandler(JewelryService service) : CollectionHandler(service)
{
    protected override string HandlerId => "collect_brands";
    protected override string[] Sources(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any()) throw new ArgumentException("品牌采集不接受参数。");
        return JewelryBrands.CollectionSources;
    }
}
