namespace KaungMcp.Jewelry;

// Explicit allowlist: persisted provenance and external error text never cross the MCP boundary.
public sealed record PublicJewelryInstrument(string Id, string Name, string Kind, string Region, string[] Categories, string? ContractMonth);
public sealed record PublicJewelryCatalog(PublicJewelryInstrument[] Instruments, string Notice);
public sealed record PublicJewelryQuote(Guid Id, string InstrumentId, string Name, string Kind, string Region,
    string Category, string? ProductVariant, string? Purity, string Product, string PriceType, decimal Price,
    string Currency, string Unit, string? ContractMonth, DateOnly? QuoteDate, DateTimeOffset? QuoteTimestamp,
    DateTimeOffset CollectedAt, string? LaborFeeIncluded);
public sealed record PublicJewelryQuoteView(PublicJewelryQuote Quote, string DataState, bool CacheStale);
public sealed record PublicJewelryQuoteItem(string InstrumentId, string Status, PublicJewelryQuoteView[] Quotes,
    PublicJewelryQuoteView[] LatestAvailable, string? Reason);
public sealed record PublicJewelryQuoteResponse(string RequestedDate, PublicJewelryQuoteItem[] Items);
public sealed record PublicJewelryHistory(string InstrumentId, string StartDate, string EndDate, string DateBasis,
    PublicJewelryQuoteView[] Items, int? NextOffset, string Notice);
public sealed record PublicJewelryComparisonRow(Guid QuoteId, decimal Price, decimal Difference, decimal DifferencePercent);
public sealed record PublicJewelryComparison(string Mode, PublicJewelryComparisonRow[] Rows, string[] Warnings, string Meaning);
public sealed record PublicJewelryConversion(decimal OriginalPrice, string FromCurrency, string FromUnit,
    decimal ConvertedPrice, string ToCurrency, string ToUnit, decimal ExchangeRate,
    DateTimeOffset? ExchangeRateTimestamp, string Formula);

public static class JewelryPublic
{
    public static string Name(JewelryInstrument instrument) => instrument.Id == "XAU/USD"
        ? "国际现货黄金" : instrument.Name;
    public static PublicJewelryCatalog Catalog() => new(JewelryCatalog.Instruments.Select(i =>
        new PublicJewelryInstrument(i.Id, Name(i), i.Kind, i.Region, i.Categories, i.ContractMonth)).ToArray(),
        "参考报价；部分品种暂无报价，产品工费可能另计。");
    public static PublicJewelryQuoteView View(JewelryQuoteView view)
    {
        var q = view.Quote;
        var name = Name(JewelryCatalog.Instruments.Single(i => i.Id == q.InstrumentId));
        // Product names come from a normalized catalog, never arbitrary upstream text.
        var product = q.Category switch { "jewelry" => "黄金首饰", "bar" => "黄金金条", "recycling" => "黄金回收", _ => name };
        return new(new(q.Id, q.InstrumentId, name, q.Kind, q.Region, q.Category, q.ProductVariant, q.Purity,
            product, q.PriceType, q.Price, q.Currency, q.Unit, q.ContractMonth, q.SourceDate,
            q.SourceTimestamp, q.CollectedAt, q.LaborFeeIncluded), view.DataState, view.CacheStale);
    }
    public static PublicJewelryQuoteResponse Quotes(JewelryQuoteResponse response) => new(response.RequestedDate,
        response.Items.Select(i => new PublicJewelryQuoteItem(i.InstrumentId, i.Status,
            i.Quotes.Select(View).ToArray(), i.LatestAvailable.Select(View).ToArray(),
            i.Quotes.Length > 0 ? i.Status == "cached_fallback" ? "当前返回已保存报价。" : null
                : response.RequestedDate == "latest" ? "暂无报价。" : "当日未更新。")).ToArray());
    public static PublicJewelryComparison Comparison(JewelryComparisonResponse response) => new(response.Mode,
        response.Rows.Select(r => new PublicJewelryComparisonRow(r.QuoteId, r.Price, r.Difference, r.DifferencePercent)).ToArray(),
        response.Warnings, response.Meaning);
    public static PublicJewelryHistory History(JewelryHistoryResponse response) => new(response.InstrumentId,
        response.StartDate, response.EndDate, response.DateBasis, response.Items.Select(View).ToArray(),
        response.NextOffset, "已保存的报价记录，可能存在缺失日期，不进行价格插补。");
}
