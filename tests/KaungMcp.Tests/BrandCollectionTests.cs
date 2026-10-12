using System.Collections.Concurrent;
using System.Text.Json;
using KaungMcp.Data;
using KaungMcp.Jewelry;
using KaungMcp.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaungMcp.Tests;

public sealed class BrandCollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jewelry", name + ".html"));

    [Theory]
    [InlineData("sojinjia")]
    [InlineData("zhenshepin")]
    [InlineData("jxgjtz")]
    public void Verified_live_samples_preserve_per_row_dates_and_gold_scope(string source)
    {
        var rows = BrandFallbackParsers.Parse(source, Fixture(source), "https://example.test/brands");
        Assert.NotEmpty(rows);
        Assert.All(rows, q =>
        {
            Assert.Equal("CN", q.Region);
            Assert.Equal("CNY", q.Currency);
            Assert.Equal("gram", q.Unit);
            Assert.NotNull(q.SourceDate);
            Assert.DoesNotContain("铂", q.RawProductName!);
            Assert.DoesNotContain("换", q.RawProductName!);
        });
        JewelryService.ValidateBatch(source, rows, Now);
        if (source == "sojinjia")
        {
            // Page date is October 9; the actual mainland cards still quote October 8.
            Assert.Equal(new DateOnly(2026, 10, 8), rows.Single(q => q.InstrumentId == "BRAND:chowtaifook").SourceDate);
            Assert.DoesNotContain(rows, q => q.Currency == "HKD");
        }
        if (source == "jxgjtz")
            Assert.Equal(1255m, rows.Single(q => q.InstrumentId == "BRAND:chowtaifook").Price);
    }

    [Fact]
    public void Synthetic_undated_source_and_changed_layout_are_not_accepted()
    {
        Assert.False(JewelryCatalog.Sources.Single(s => s.Id == "cngolddaily").AdapterAvailable);
        Assert.Throws<FormatException>(() => BrandFallbackParsers.Parse("cngolddaily", Fixture("cngolddaily"), "https://example.test"));
        Assert.Throws<FormatException>(() => BrandFallbackParsers.Parse("sojinjia", "<html>captcha</html>", "https://example.test"));
        var rows = BrandFallbackParsers.Parse("zhenshepin", Fixture("zhenshepin").Replace("1249元/克", "-元/克"), "https://example.test");
        Assert.DoesNotContain(rows, q => q.Price <= 0);
    }

    [Fact]
    public async Task Official_categories_are_preserved_and_first_fallback_fills_only_gaps_once()
    {
        var adapter = new BrandAdapter();
        await using var factory = Factory(adapter);
        using var http = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        await service.CollectBrandsAsync(false, default);
        Assert.Equal(1, adapter.Calls["sojinjia"]);
        Assert.False(adapter.Calls.ContainsKey("zhenshepin"));
        Assert.False(adapter.Calls.ContainsKey("jxgjtz"));
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<JewelryDbContext>>().CreateDbContextAsync();
        var records = await db.JewelryQuotes.ToListAsync();
        Assert.DoesNotContain(records, q => q.InstrumentId == "BRAND:chowsangsang" && q.SourceId == "sojinjia" &&
            JsonSerializer.Deserialize<JewelryQuote>(q.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Category == "jewelry");
        Assert.Contains(records, q => q.InstrumentId == "BRAND:chowsangsang" && q.SourceId == "sojinjia");
        Assert.Contains(records, q => q.InstrumentId == "BRAND:laofengxiang" && q.SourceId == "sojinjia");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.CollectBrandsAsync(false, default)));
        Assert.Equal(1, adapter.Calls["sojinjia"]);
        Assert.Equal(records.Count, await db.JewelryQuotes.CountAsync());
        var response = await service.GetQuotesAsync(true, ["BRAND:chowsangsang"], "today", "jewelry", "CN", default);
        Assert.Equal("chowsangsang", Assert.Single(response.Items[0].Quotes).Quote.SourceId);
        var publicJson = JsonSerializer.Serialize(JewelryPublic.Quotes(response), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("source", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", publicJson);
    }

    [Fact]
    public async Task Failure_and_prior_date_continue_to_next_provider_without_storing_old_fallback()
    {
        var adapter = new BrandAdapter { FailFirst = true, OldSecond = true };
        await using var factory = Factory(adapter);
        using var http = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        var results = await service.CollectBrandsAsync(false, default);
        Assert.Contains(results, r => r.SourceId == "sojinjia" && r.Status == "failed");
        Assert.Equal(1, adapter.Calls["zhenshepin"]);
        Assert.Equal(1, adapter.Calls["jxgjtz"]);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<JewelryDbContext>>().CreateDbContextAsync();
        Assert.False(await db.JewelryQuotes.AnyAsync(q => q.SourceId == "zhenshepin"));
        Assert.True(await db.JewelryQuotes.AnyAsync(q => q.SourceId == "jxgjtz"));
        // A later official update takes priority without deleting fallback history.
        adapter.FullOfficial = true;
        await service.CollectBrandsAsync(true, default);
        var response = await service.GetQuotesAsync(true, ["BRAND:chowsangsang"], "today", "bar", "CN", default);
        Assert.Equal("chowsangsang", Assert.Single(response.Items[0].Quotes).Quote.SourceId);
        Assert.True(await db.JewelryQuotes.AnyAsync(q => q.SourceId == "jxgjtz" && q.InstrumentId == "BRAND:chowsangsang"));
    }

    [Fact]
    public async Task All_fallbacks_missing_today_return_missing_not_yesterday()
    {
        var adapter = new BrandAdapter { OldAll = true };
        await using var factory = Factory(adapter);
        using var http = factory.CreateClient();
        var service = factory.Services.GetRequiredService<JewelryService>();
        await service.CollectBrandsAsync(false, default);
        var response = await service.GetQuotesAsync(true, ["BRAND:laofengxiang"], "today", "jewelry", "CN", default);
        Assert.Empty(response.Items[0].Quotes);
        Assert.Equal("date_missing", response.Items[0].Status);
    }

    private static McpWebApplicationFactory Factory(BrandAdapter adapter) => new(services =>
    {
        services.AddSingleton<IJewelryAdapter>(adapter);
        services.AddSingleton<TimeProvider>(new Clock());
    });
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class BrandAdapter : IJewelryAdapter
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public bool FailFirst { get; init; }
        public bool OldSecond { get; init; }
        public bool OldAll { get; init; }
        public bool FullOfficial { get; set; }
        public Task<IReadOnlyList<JewelryQuote>> CollectAsync(JewelrySourceDefinition source, CancellationToken ct)
        {
            Calls.AddOrUpdate(source.Id, 1, (_, count) => count + 1);
            if (source.Id == "sojinjia" && FailFirst) throw new HttpRequestException("https://private-debug.example/ upstream failure");
            if (!JewelryBrands.IsFallback(source.Id))
                return Task.FromResult<IReadOnlyList<JewelryQuote>>(new[] { "jewelry", "bar", "recycling" }
                    .Where(c => FullOfficial || c == "jewelry").Select(c => Quote("BRAND:" + source.Id, source.Id, c, Today)).ToArray());
            var date = OldAll || source.Id == "zhenshepin" && OldSecond ? Today.AddDays(-1) : Today;
            return Task.FromResult<IReadOnlyList<JewelryQuote>>(JewelryBrands.All.SelectMany(b =>
                new[] { "jewelry", "bar", "recycling" }.Select(c => Quote(b.InstrumentId, source.Id, c, date))).ToArray());
        }
        private static JewelryQuote Quote(string id, string source, string category, DateOnly date) => new()
        {
            InstrumentId = id, SourceId = source, SourceUrl = "https://private-debug.example/", Kind = "brand",
            Price = 1200, Category = category, PriceType = category == "recycling" ? "buy" : "sell", SourceDate = date,
            RawProductName = "private upstream name"
        };
    }
}
