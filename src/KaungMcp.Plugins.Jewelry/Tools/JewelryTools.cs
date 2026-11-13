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
    [McpServerTool(Name = "kaung_jewelry_list_instruments", Title = "行情品种目录", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("查询支持的市场行情及中国大陆黄金品牌的标识和类别。按标识查询前应先调用此工具；列出的类别不保证有可用报价。")]
    public Task<PublicJewelryCatalog> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(JewelryPublic.Catalog());

    [McpServerTool(Name = "kaung_jewelry_get_market_quotes", Title = "市场行情查询", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("查询国内每日收盘价、基准价及国际 XAU/USD 价格，包含报价日期与数据时效信息。缓存缺失或过期时触发采集，不会用其他日期的价格替代所请求日期的价格。")]
    public async Task<PublicJewelryQuoteResponse> MarketAsync(
        [Description("Optional IDs from list_instruments; omitted or empty selects all markets.")] string[]? instrumentIds = null,
        [Description("latest (default), today (Asia/Shanghai calendar date), or yyyy-MM-dd. Compared to each quote's quoteDate; global spot uses UTC quote date.")] string date = "latest",
        CancellationToken cancellationToken = default) => JewelryPublic.Quotes(await service.GetQuotesAsync(false, instrumentIds, date, null, "CN", cancellationToken));

    [McpServerTool(Name = "kaung_jewelry_get_brand_quotes", Title = "品牌报价查询", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("查询中国大陆品牌黄金参考价，区分首饰、金条与回收类别，并说明费用及适用限制。逐项报告缺失的品牌与日期，将最新可用报价 latestAvailable 与所请求日期明确区分。参考价不代表门店最终成交报价。")]
    public async Task<PublicJewelryQuoteResponse> BrandsAsync(
        [Description("Optional BRAND:* instrument IDs from list_instruments; omitted selects all supported brands.")] string[]? brandIds = null,
        [Description("latest, today (Asia/Shanghai), or yyyy-MM-dd.")] string date = "latest",
        [Description("Optional category: jewelry, bar, recycling. Omitted selects all available categories.")] string? category = null,
        [Description("Only CN (mainland China) is supported.")] string region = "CN",
        CancellationToken cancellationToken = default) => JewelryPublic.Quotes(await service.GetQuotesAsync(true, brandIds, date, category, region, cancellationToken));

    [McpServerTool(Name = "kaung_jewelry_get_price_history", Title = "历史价格查询", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("分页查询已保存的报价观测记录，不提供历史回溯补采、缺失数据填补或完整历史档案保证。报价日期未知的记录可通过 collected_date 查询。")]
    public async Task<PublicJewelryHistory> HistoryAsync(
        [Description("Exact market or BRAND:* ID from list_instruments.")] string instrumentId,
        [Description("Inclusive yyyy-MM-dd.")] string startDate,
        [Description("Inclusive yyyy-MM-dd; maximum span 367 days.")] string endDate,
        [Description("source_date (default), or collected_date using UTC collection dates.")] string dateBasis = "source_date",
        [Description("Zero-based pagination offset.")] int offset = 0,
        [Description("Page size 1..500.")] int limit = 100,
        CancellationToken cancellationToken = default) => JewelryPublic.History(await service.HistoryAsync(instrumentId, startDate, endDate, dateBasis, offset, limit, cancellationToken));

    [McpServerTool(Name = "kaung_jewelry_compare_prices", Title = "价格对比", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("比较 2 至 20 个已保存的报价标识。rank 排名要求品牌价格类型、类别、纯度、费用、地区及日期口径均已知且一致；reference 仅返回相对首个报价的数值差，不进行排名或利润解读。两种模式均要求币种、单位一致，且报价日期已知并相同。不得将品牌价与市场价的差额等同于利润。")]
    public async Task<PublicJewelryComparison> CompareAsync(
        [Description("Distinct quote.id values returned by quote or history tools.")] Guid[] quoteIds,
        [Description("rank (default) or reference; unknown purity/fees prevent rank.")] string mode = "rank",
        CancellationToken cancellationToken = default) => JewelryPublic.Comparison(await service.CompareAsync(quoteIds, mode, cancellationToken));

    [McpServerTool(Name = "kaung_jewelry_convert_price", Title = "价格换算", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("换算以每克或每金衡盎司计价的正数价格，可使用调用方明确提供的汇率进行币种换算。汇率表示每 1 单位原币种对应的目标币种金额。返回原始输入与计算公式，不获取或推定汇率，也不调整纯度、税费或溢价。")]
    public PublicJewelryConversion Convert(
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
        var converted = JewelryService.Convert(price, fromCurrency, fromUnit, toCurrency, toUnit, exchangeRate,
            exchangeRateSource, timestamp, decimals);
        return new(converted.OriginalPrice, converted.FromCurrency, converted.FromUnit, converted.ConvertedPrice,
            converted.ToCurrency, converted.ToUnit, converted.ExchangeRate, converted.ExchangeRateTimestamp, converted.Formula);
    }

    [McpServerTool(Name = "kaung_jewelry_get_source_status", Title = "采集来源状态", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("查看数据来源的验证依据、启用状态、最近尝试与成功时间、下次尝试时间、退避情况、错误及滚动七天采集次数。来源可访问不代表价格为最新，也不能证明连续七天稳定。")]
    public Task<JewelrySourceStatus[]> StatusAsync(CancellationToken cancellationToken = default) => service.StatusAsync(cancellationToken);
}
