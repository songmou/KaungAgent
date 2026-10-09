using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using KaungMcp.Jewelry;
using KaungMcp.Security;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tools;

[McpServerToolType]
[McpToolCategory("jewelry", "珠宝行情", 30, "贵金属与品牌报价工具")]
[Authorize(Policy = SecurityConstants.McpClientPolicy)]
public sealed class JewelryTools(JewelryService service)
{
    [McpServerTool(Name = "kaung_jewelry_list_instruments", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Discover jewelry market/brand identifiers, planned categories and current source availability. futures_family is a directory placeholder, never a priced contract. Unavailable sources remain discoverable. Call this before querying IDs.")]
    public async Task<JewelryCatalogResponse> ListAsync(CancellationToken cancellationToken = default) => new(
        JewelryCatalog.Instruments,
        await service.StatusAsync(cancellationToken),
        "Categories describe query scope, not guaranteed source coverage. Only enabled verified adapters collect data. No real-time or seven-day stability guarantee.");

    [McpServerTool(Name = "kaung_jewelry_get_market_quotes", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Read SGE daily closes/benchmarks and provider XAU/USD quotes with provenance, timestamps and freshness. COMEX is unavailable until a concrete-contract source is validated. Reads cache; missing/expired caches trigger bounded collection with stale fallback. Never substitute yesterday for today, futures for spot, or collection time for source time.")]
    public Task<JewelryQuoteResponse> MarketAsync(
        [Description("Optional IDs from list_instruments; omitted or empty selects all markets.")] string[]? instrumentIds = null,
        [Description("latest (default), today (Asia/Shanghai calendar date), or yyyy-MM-dd. Compared to each quote's sourceDate/dateBasis; global spot uses UTC source date.")] string date = "latest",
        CancellationToken cancellationToken = default) => service.GetQuotesAsync(false, instrumentIds, date, null, "CN", cancellationToken);

    [McpServerTool(Name = "kaung_jewelry_get_brand_quotes", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Read mainland brand reference gold prices. Distinguishes jewelry, bars and recycling; includes fee/applicability caveats. Reports each missing source/date separately, with latestAvailable explicitly separate from the requested date. Not a final store transaction offer.")]
    public Task<JewelryQuoteResponse> BrandsAsync(
        [Description("Optional BRAND:* instrument IDs from list_instruments; omitted selects all eight brands.")] string[]? brandIds = null,
        [Description("latest, today (Asia/Shanghai), or yyyy-MM-dd.")] string date = "latest",
        [Description("Optional category: jewelry, bar, recycling. Omitted selects all available categories.")] string? category = null,
        [Description("Only CN (mainland China) is supported.")] string region = "CN",
        CancellationToken cancellationToken = default) => service.GetQuotesAsync(true, brandIds, date, category, region, cancellationToken);

    [McpServerTool(Name = "kaung_jewelry_get_price_history", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Query persisted observations with pagination. official_daily identifies official dated records; collected_snapshot identifies observed prices, never synthetic closing prices. No historical backfill, gap filling or complete archive guarantee. Unknown source dates can be found with collected_date.")]
    public Task<JewelryHistoryResponse> HistoryAsync(
        [Description("Exact market or BRAND:* ID from list_instruments.")] string instrumentId,
        [Description("Inclusive yyyy-MM-dd.")] string startDate,
        [Description("Inclusive yyyy-MM-dd; maximum span 367 days.")] string endDate,
        [Description("source_date (default), or collected_date using UTC collection dates.")] string dateBasis = "source_date",
        [Description("Zero-based pagination offset.")] int offset = 0,
        [Description("Page size 1..500.")] int limit = 100,
        CancellationToken cancellationToken = default) => service.HistoryAsync(instrumentId, startDate, endDate, dateBasis, offset, limit, cancellationToken);

    [McpServerTool(Name = "kaung_jewelry_compare_prices", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Compare 2..20 stored quote IDs. rank requires matching known brand price/category/purity/fee/region/date basis. reference returns numerical differences from the first quote without ranking or margin interpretation. Both require same currency/unit and known matching dates. Never equate brand-vs-market differences with profit.")]
    public Task<JewelryComparisonResponse> CompareAsync(
        [Description("Distinct quote.id values returned by quote or history tools.")] Guid[] quoteIds,
        [Description("rank (default) or reference; unknown purity/fees prevent rank.")] string mode = "rank",
        CancellationToken cancellationToken = default) => service.CompareAsync(quoteIds, mode, cancellationToken);

    [McpServerTool(Name = "kaung_jewelry_convert_price", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Convert a positive price per gram/troy ounce and optionally currency using explicit caller-provided FX. Rate is TARGET currency per one SOURCE currency. Returns original input and formula; no fetched or implied exchange rate, no purity/tax/premium adjustment.")]
    public JewelryConversion Convert(
        [Description("Positive price per fromUnit.")] decimal price,
        [Description("Uppercase three-letter source currency, e.g. USD.")] string fromCurrency,
        [Description("gram or troy_ounce (31.1034768 grams). Ordinary ounces are not supported.")] string fromUnit,
        [Description("Uppercase three-letter target currency, e.g. CNY.")] string toCurrency,
        [Description("gram or troy_ounce.")] string toUnit,
        [Description("Target currency per 1 source currency, required for different currencies.")] decimal? exchangeRate = null,
        [Description("Name/URL of caller's FX source, required for different currencies.")] string? exchangeRateSource = null,
        [Description("ISO-8601 FX timestamp with Z or explicit offset, required for different currencies.")] string? exchangeRateTimestamp = null,
        [Description("Decimal places 0..8; AwayFromZero midpoint rounding.")] int decimals = 4)
    {
        DateTimeOffset? timestamp = null;
        if (exchangeRateTimestamp is not null)
        {
            if (!Regex.IsMatch(exchangeRateTimestamp, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                !DateTimeOffset.TryParse(exchangeRateTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new McpException("exchangeRateTimestamp must be ISO-8601 with an explicit time-zone offset.");
            timestamp = parsed;
        }
        return JewelryService.Convert(price, fromCurrency, fromUnit, toCurrency, toUnit, exchangeRate,
            exchangeRateSource, timestamp, decimals);
    }

    [McpServerTool(Name = "kaung_jewelry_get_source_status", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Inspect source validation evidence, enabled state, last attempt/success, next attempt, backoff, errors and rolling seven-day collection counts. A reachable source is not proof of fresh prices or seven-day stability.")]
    public Task<JewelrySourceStatus[]> StatusAsync(CancellationToken cancellationToken = default) => service.StatusAsync(cancellationToken);
}
