using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.Data.Entities;
using KaungMcp.Jewelry;
using KaungMcp.Security;
using KaungMcp.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace KaungMcp.Tests;

public sealed class JewelryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T06:00:00Z");
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jewelry", name));
    private static readonly Dictionary<string, string> Files = new()
    {
        ["sge_daily"] = "sge-daily.html", ["sge_benchmark"] = "sge-benchmark.html",
        ["gold_api"] = "gold-api.json", ["chowsangsang"] = "chowsangsang.html", ["lukfook"] = "lukfook-cn.json"
    };
    private static IReadOnlyList<JewelryQuote> Parse(string id) => JewelryParsers.Parse(id, Fixture(Files[id]),
        JewelryCatalog.Sources.Single(s => s.Id == id).Endpoint!);

    [Fact]
    public void Official_fixtures_preserve_price_types_dates_units_and_provenance()
    {
        var sge = Parse("sge_daily");
        Assert.Equal(2, sge.Count);
        Assert.Equal(926.01m, sge.Single(q => q.InstrumentId == "SGE:Au99.99").Price);
        Assert.All(sge, q =>
        {
            Assert.Equal("close", q.PriceType);
            Assert.Equal("official_daily", q.HistoryKind);
            Assert.Equal(new DateOnly(2026, 9, 24), q.SourceDate);
            Assert.Null(q.SourceTimestamp);
        });
        var benchmark = Parse("sge_benchmark");
        Assert.Equal(936.31m, benchmark.Single(q => q.PriceType == "benchmark_am").Price);
        Assert.Equal(935.50m, benchmark.Single(q => q.PriceType == "benchmark_pm").Price);
        var spot = Assert.Single(Parse("gold_api"));
        Assert.Equal("troy_ounce", spot.Unit);
        Assert.Equal("USD", spot.Currency);
        Assert.NotNull(spot.SourceTimestamp);
        Assert.Equal("spot", spot.Kind);
        var css = Parse("chowsangsang");
        var jewelry = css.Single(q => q.Category == "jewelry");
        Assert.Equal(new DateOnly(2026, 9, 26), jewelry.SourceDate);
        Assert.Equal(1294m, jewelry.Price);
        Assert.DoesNotContain(css, q => q.RawProductName!.Contains("换金", StringComparison.Ordinal));
        var lf = Parse("lukfook");
        Assert.Equal(1290m, lf.Single(q => q.Category == "jewelry").Price);
        Assert.Equal("excluded", lf.Single(q => q.Category == "jewelry").LaborFeeIncluded);
        Assert.Null(lf.Single(q => q.Category == "jewelry").Purity);
        Assert.Equal(918m, lf.Single(q => q.Category == "recycling").Price);
        foreach (var id in Files.Keys) JewelryService.ValidateBatch(id, Parse(id), Now);
    }

    [Fact]
    public void Parsers_reject_HK_wrong_symbols_missing_timestamps_and_page_changes()
    {
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("lukfook", Fixture("lukfook-hk.json"), "https://example.test"));
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("sge_daily", "<html>captcha</html>", "https://example.test"));
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("sge_benchmark", "<tr><th>轮次编号</th></tr>", "https://example.test"));
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("gold_api", Fixture("gold-api.json").Replace("XAU", "XAG"), "https://example.test"));
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("gold_api", Fixture("gold-api.json").Replace("31Z", "31"), "https://example.test"));
        Assert.Throws<FormatException>(() => JewelryParsers.Parse("gold_api", Fixture("gold-api.json").Replace("4286.200195", "0"), "https://example.test"));
        var zero = Fixture("sge-daily.html").Replace("926.01", "0.00");
        Assert.DoesNotContain(JewelryParsers.Parse("sge_daily", zero, "https://example.test"), q => q.InstrumentId == "SGE:Au99.99");
    }

    [Fact]
    public void CTF_parser_keeps_craft_and_investment_bars_separate_but_source_stays_disabled()
    {
        var quotes = JewelryParsers.Parse("chowtaifook", Fixture("chowtaifook.json"), "https://api2.ctfmall.com/");
        Assert.Equal(4, quotes.Count);
        var bars = quotes.Where(q => q.Category == "bar").ToArray();
        Assert.Equal(2, bars.Select(q => q.SeriesKey).Distinct().Count());
        Assert.Equal(1139m, bars.Single(q => q.ProductVariant == "craft_bar").Price);
        Assert.Equal(999m, bars.Single(q => q.ProductVariant == "investment_gold").Price);
        Assert.DoesNotContain(quotes, q => q.RawProductName!.Contains("增值", StringComparison.Ordinal));
        JewelryService.ValidateBatch("chowtaifook", quotes, Now);
        var source = JewelryCatalog.Sources.Single(s => s.Id == "chowtaifook");
        Assert.True(source.AdapterAvailable);
        Assert.False(PureService(new TestClock(Now)).Enabled(source));
    }

    [Fact]
    public void Batch_validation_rejects_future_dates_duplicate_series_and_contract_families()
    {
        var spot = Assert.Single(Parse("gold_api"));
        Assert.Throws<FormatException>(() => JewelryService.ValidateBatch("gold_api", [spot with { SourceDate = new DateOnly(2026, 9, 28) }], Now));
        Assert.Throws<FormatException>(() => JewelryService.ValidateBatch("gold_api", [spot, spot], Now));
        Assert.Throws<FormatException>(() => JewelryService.ValidateBatch("cme", [spot with
        { InstrumentId = "COMEX:GC", SourceId = "cme", Region = "US", Kind = "futures_family" }], Now));
    }

    [Fact]
    public void Conversion_preserves_FX_direction_troy_units_and_decimal_precision()
    {
        var converted = JewelryService.Convert(3110.34768m, "USD", "troy_ounce", "CNY", "gram", 7m, "test-fx", Now, 4);
        Assert.Equal(700m, converted.ConvertedPrice);
        Assert.Equal(3110.34768m, converted.OriginalPrice);
        Assert.Equal("test-fx", converted.ExchangeRateSource);
        var back = JewelryService.Convert(100m, "USD", "gram", "USD", "troy_ounce", null, null, null, 8);
        Assert.Equal(3110.34768m, back.ConvertedPrice);
        Assert.Equal(1.01m, JewelryService.Convert(1.005m, "CNY", "gram", "CNY", "gram", null, null, null, 2).ConvertedPrice);
        Assert.Throws<McpException>(() => JewelryService.Convert(100m, "USD", "gram", "CNY", "gram", null, null, null, 4));
        Assert.Throws<McpException>(() => JewelryService.Convert(100m, "USD", "ounce", "USD", "gram", null, null, null, 4));
        Assert.Throws<McpException>(() => JewelryService.Convert(100m, "USD", "gram", "USD", "gram", 7m, null, null, 4));
        Assert.Throws<McpException>(() => JewelryService.Convert(decimal.MaxValue, "USD", "gram", "USD", "troy_ounce", null, null, null, 4));
    }

    [Fact]
    public void Ranking_requires_known_matching_basis_but_reference_differences_are_not_profit()
    {
        var clock = new TestClock(Now);
        var service = PureService(clock);
        var first = Parse("chowsangsang").Single(q => q.Category == "jewelry") with
        { CollectedAt = Now, LaborFeeIncluded = "excluded", Price = 1000m };
        var second = first with { Id = Guid.NewGuid(), InstrumentId = "BRAND:lukfook", SourceId = "lukfook", Price = 1100m };
        var result = service.Compare([second, first], "rank");
        Assert.Equal(first.Id, result.Rows[0].QuoteId);
        Assert.Equal(10m, result.Rows[1].DifferencePercent);
        Assert.Throws<McpException>(() => service.Compare([first, second with { Purity = null }], "rank"));
        Assert.Throws<McpException>(() => service.Compare([first, second with { Category = "bar" }], "rank"));
        Assert.Throws<McpException>(() => service.Compare([first, second with { LaborFeeIncluded = null }], "rank"));
        Assert.Throws<McpException>(() => service.Compare([first, second with { SourceDate = null }], "reference"));
        var reference = service.Compare([first, second with { Purity = null, DateBasis = "exchange_trade_date" }], "reference");
        Assert.Contains("no ranking", reference.Meaning, StringComparison.Ordinal);
        Assert.NotEmpty(reference.Warnings);
    }

    [Fact]
    public void Freshness_distinguishes_UTC_China_date_unknown_time_and_cache_age()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-26T16:05:00Z"));
        var service = PureService(clock);
        var quote = Parse("chowsangsang").Single(q => q.Category == "jewelry") with { CollectedAt = clock.GetUtcNow() };
        Assert.Equal("prior_date", service.View(quote).DataState);
        Assert.Equal("unknown_time", service.View(quote with { SourceDate = null, SourceTimestamp = null }).DataState);
        Assert.Equal("date_only", service.View(quote with { SourceDate = new DateOnly(2026, 9, 27), SourceTimestamp = null }).DataState);
        Assert.Equal("stale_cache", service.View(quote with { CollectedAt = clock.GetUtcNow().AddHours(-3) }).DataState);
    }

    [Fact]
    public async Task Collection_persists_snapshots_backoff_partial_success_and_recovers_without_losing_cache()
    {
        var clock = new TestClock(Now);
        var adapter = new FixtureAdapter();
        await using var factory = Factory(adapter, clock);
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        await service.CollectDueAsync(null, default);
        var sources = await service.StatusAsync(default);
        Assert.Equal(5, sources.Count(s => s.RuntimeStatus == "ok"));
        Assert.False(sources.Single(s => s.Source.Id == "cme").Enabled);
        var today = await service.GetQuotesAsync(true, ["BRAND:lukfook"], "today", "jewelry", "CN", default);
        Assert.Equal("date_missing", today.Items[0].Status);
        Assert.Empty(today.Items[0].Quotes);
        Assert.Equal(new DateOnly(2026, 9, 26), Assert.Single(today.Items[0].LatestAvailable).Quote.SourceDate);
        var before = adapter.Calls;
        await service.GetQuotesAsync(false, ["XAU/USD"], "latest", null, "CN", default);
        Assert.Equal(before, adapter.Calls);
        clock.Advance(TimeSpan.FromHours(2));
        adapter.FailGold = true;
        await service.CollectDueAsync(null, default);
        sources = await service.StatusAsync(default);
        Assert.Equal("error", sources.Single(s => s.Source.Id == "gold_api").RuntimeStatus);
        Assert.Equal(1, sources.Single(s => s.Source.Id == "gold_api").ConsecutiveFailures);
        Assert.Equal("ok", sources.Single(s => s.Source.Id == "lukfook").RuntimeStatus);
        var cached = await service.GetQuotesAsync(false, ["XAU/USD"], "latest", null, "CN", default);
        Assert.Equal("cached_fallback", cached.Items[0].Status);
        Assert.Contains("HTTP 429", cached.Items[0].Reason!, StringComparison.Ordinal);
        Assert.Equal("stale_cache", Assert.Single(cached.Items[0].Quotes).DataState);
        before = adapter.Calls;
        await service.CollectDueAsync(["gold_api"], default);
        Assert.Equal(before, adapter.Calls);
        clock.Advance(TimeSpan.FromMinutes(31));
        adapter.FailGold = false;
        await service.CollectDueAsync(["gold_api"], default);
        Assert.Equal(0, (await service.StatusAsync(default)).Single(s => s.Source.Id == "gold_api").ConsecutiveFailures);
        var history = await service.HistoryAsync("XAU/USD", "2026-09-27", "2026-09-27", "source_date", 0, 1, default);
        Assert.Single(history.Items);
        Assert.Equal(1, history.NextOffset);
        Assert.Single((await service.HistoryAsync("XAU/USD", "2026-09-27", "2026-09-27", "source_date", 1, 1, default)).Items);
    }

    [Fact]
    public async Task Concurrent_cold_queries_are_coalesced_and_disabled_sources_never_collect()
    {
        var adapter = new FixtureAdapter();
        await using var factory = Factory(adapter, new TestClock(Now));
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetQuotesAsync(false, ["XAU/USD", "COMEX:GC"], "latest", null, "CN", default)));
        Assert.Equal(1, adapter.Calls);
        Assert.DoesNotContain("cme", adapter.CalledSources);
        Assert.All(results, r => Assert.Equal("available", r.Items.Single(i => i.InstrumentId == "XAU/USD").Status));
    }

    [Fact]
    public async Task Timeout_and_invalid_batches_are_logged_without_replacing_good_observations()
    {
        var adapter = new FixtureAdapter();
        var clock = new TestClock(Now);
        await using var factory = new McpWebApplicationFactory(services =>
        {
            services.AddSingleton<IJewelryAdapter>(adapter);
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<JewelryOptions>(o => o.TimeoutSeconds = 1);
        });
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        await service.CollectDueAsync(["gold_api"], default);
        clock.Advance(TimeSpan.FromMinutes(16));
        adapter.TimeoutGold = true;
        await service.CollectDueAsync(["gold_api"], default);
        Assert.Contains("timed out", (await service.StatusAsync(default)).Single(s => s.Source.Id == "gold_api").LastError!, StringComparison.Ordinal);
        adapter.TimeoutGold = false;
        adapter.InvalidGold = true;
        clock.Advance(TimeSpan.FromMinutes(31));
        await service.CollectDueAsync(["gold_api"], default);
        var history = await service.HistoryAsync("XAU/USD", "2026-09-27", "2026-09-27", "collected_date", 0, 100, default);
        Assert.Single(history.Items);
        Assert.Equal(2, (await service.StatusAsync(default)).Single(s => s.Source.Id == "gold_api").ConsecutiveFailures);
    }

    [Fact]
    public async Task Unknown_source_dates_remain_null_and_are_only_in_collection_date_history()
    {
        var adapter = new FixtureAdapter { UnknownGoldTime = true };
        await using var factory = Factory(adapter, new TestClock(Now));
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        var latest = await service.GetQuotesAsync(false, ["XAU/USD"], "latest", null, "CN", default);
        var quote = Assert.Single(latest.Items[0].Quotes);
        Assert.Equal("unknown_time", quote.DataState);
        Assert.Null(quote.Quote.SourceTimestamp);
        Assert.Null(quote.Quote.SourceDate);
        Assert.Empty((await service.HistoryAsync("XAU/USD", "2026-09-27", "2026-09-27", "source_date", 0, 100, default)).Items);
        Assert.Single((await service.HistoryAsync("XAU/USD", "2026-09-27", "2026-09-27", "collected_date", 0, 100, default)).Items);
    }

    [Fact]
    public async Task Migration_upgrades_old_database_without_changing_clients_and_model_has_no_pending_changes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new KaungMcpDbContext(new DbContextOptionsBuilder<KaungMcpDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260923065633_AddInvocationLogPayloads");
        var id = Guid.NewGuid();
        db.ClientApplications.Add(new McpClientApplication
        {
            Id = id, Name = "existing-client", KeyId = "existing-key", KeyHash = new byte[32], IsEnabled = true,
            CreatedAtUtc = Now, UpdatedAtUtc = Now
        });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("existing-client", (await db.ClientApplications.SingleAsync(c => c.Id == id)).Name);
        Assert.Empty(await db.JewelryQuotes.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Adapters_apply_robots_and_mainland_header_and_reject_rate_limit()
    {
        var fetch = new StubFetcher();
        var robots = new StubRobots();
        var adapter = new JewelryAdapter(fetch, robots);
        var source = JewelryCatalog.Sources.Single(s => s.Id == "lukfook");
        await adapter.CollectAsync(source, default);
        Assert.Equal("sc", fetch.Headers!["lang"]);
        Assert.Equal(source.Endpoint, Assert.Single(robots.Urls));
        fetch.Status = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.CollectAsync(source, default));
        robots.Deny = true;
        var calls = fetch.Calls;
        await Assert.ThrowsAsync<WebFetchException>(() => adapter.CollectAsync(source, default));
        Assert.Equal(calls, fetch.Calls);
    }

    [Fact]
    public async Task All_seven_MCP_tools_are_discovered_structured_authorized_and_logged()
    {
        await using var factory = Factory(new FixtureAdapter(), new TestClock(Now));
        using var admin = factory.CreateClient();
        var unauthorized = await admin.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var response = await admin.PostAsJsonAsync("/api/admin/clients", new CreateClientRequest("jewelry-tests", "fixture-only"));
        var created = (await response.Content.ReadFromJsonAsync<CreatedClientResponse>())!;
        var http = factory.CreateClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + created.ApiKey }
        }, http, ownsHttpClient: true));
        var tools = (await mcp.ListToolsAsync()).Where(t => t.Name.StartsWith("kaung_jewelry_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, tools.Length);
        Assert.All(tools, t => Assert.NotNull(t.ProtocolTool.OutputSchema));
        foreach (var name in new[] { "list_instruments", "get_source_status", "get_market_quotes", "get_brand_quotes" })
        {
            var result = await mcp.CallToolAsync("kaung_jewelry_" + name, new Dictionary<string, object?>());
            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
        }
        var conversion = await mcp.CallToolAsync("kaung_jewelry_convert_price", new Dictionary<string, object?>
        {
            ["price"] = 3110.34768m, ["fromCurrency"] = "USD", ["fromUnit"] = "troy_ounce",
            ["toCurrency"] = "USD", ["toUnit"] = "gram"
        });
        Assert.NotEqual(true, conversion.IsError);
        Assert.Equal(100m, conversion.StructuredContent!.Value.GetProperty("convertedPrice").GetDecimal());
        var invalid = await mcp.CallToolAsync("kaung_jewelry_get_brand_quotes", new Dictionary<string, object?> { ["date"] = "not-a-date" });
        Assert.True(invalid.IsError);
        var history = await mcp.CallToolAsync("kaung_jewelry_get_price_history", new Dictionary<string, object?>
        { ["instrumentId"] = "XAU/USD", ["startDate"] = "2026-09-27", ["endDate"] = "2026-09-27" });
        Assert.NotEqual(true, history.IsError);
        var service = factory.Services.GetRequiredService<JewelryService>();
        var quotes = (await service.GetQuotesAsync(true, ["BRAND:chowsangsang", "BRAND:lukfook"], "2026-09-26", "jewelry", "CN", default))
            .Items.Select(i => i.Quotes[0].Quote.Id).ToArray();
        var compare = await mcp.CallToolAsync("kaung_jewelry_compare_prices", new Dictionary<string, object?> { ["quoteIds"] = quotes, ["mode"] = "reference" });
        Assert.NotEqual(true, compare.IsError);
        var logs = await McpWebApplicationFactory.WaitForLogsAsync(admin, created.Id, rows =>
            rows.Count(r => r.ToolName?.StartsWith("kaung_jewelry_", StringComparison.Ordinal) == true) >= 8);
        Assert.Contains(logs.Items, l => l.ToolName == "kaung_jewelry_get_brand_quotes" && !l.Succeeded);
        Assert.Contains(logs.Items, l => l.ToolName == "kaung_jewelry_convert_price" && l.Succeeded);
    }

    private static McpWebApplicationFactory Factory(FixtureAdapter adapter, TestClock clock) => new(services =>
    {
        services.AddSingleton<IJewelryAdapter>(adapter);
        services.AddSingleton<TimeProvider>(clock);
    });
    private static JewelryService PureService(TimeProvider clock) => new(null!, null!, Options.Create(new JewelryOptions()), clock, NullLogger<JewelryService>.Instance);
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan span) => now += span;
    }
    private sealed class FixtureAdapter : IJewelryAdapter
    {
        public int Calls;
        public bool FailGold;
        public bool TimeoutGold;
        public bool InvalidGold;
        public bool UnknownGoldTime;
        public System.Collections.Concurrent.ConcurrentBag<string> CalledSources { get; } = [];
        public async Task<IReadOnlyList<JewelryQuote>> CollectAsync(JewelrySourceDefinition source, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            CalledSources.Add(source.Id);
            if (FailGold && source.Id == "gold_api") throw new HttpRequestException("HTTP 429 fixture");
            if (TimeoutGold && source.Id == "gold_api") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var quotes = Parse(source.Id);
            if (InvalidGold && source.Id == "gold_api") return quotes.Select(q => q with { Price = 0 }).ToArray();
            if (UnknownGoldTime && source.Id == "gold_api") return quotes.Select(q => q with { SourceDate = null, SourceTimestamp = null }).ToArray();
            return quotes;
        }
    }
    private sealed class StubRobots : IRobotsPolicy
    {
        public bool Deny;
        public List<string> Urls { get; } = [];
        public Task EnsureAllowedAsync(Uri uri, CancellationToken cancellationToken)
        {
            Urls.Add(uri.AbsoluteUri);
            if (Deny) throw new WebFetchException("robots denied");
            return Task.CompletedTask;
        }
    }
    private sealed class StubFetcher : ISafeHttpFetcher
    {
        public IReadOnlyDictionary<string, string>? Headers;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        public Task<SafeHttpResponse> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SafeHttpResponse(Status, uri, "application/json", Fixture("lukfook-cn.json")));
        }
        public Task<SafeHttpResponse> FetchWithHeadersAsync(Uri uri, int maxBytes, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            Headers = headers;
            return FetchAsync(uri, maxBytes, cancellationToken);
        }
    }
}
