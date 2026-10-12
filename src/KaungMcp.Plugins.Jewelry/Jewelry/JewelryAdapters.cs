using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using KaungMcp.Web;

namespace KaungMcp.Jewelry;

public interface IJewelryAdapter
{
    Task<IReadOnlyList<JewelryQuote>> CollectAsync(JewelrySourceDefinition source, CancellationToken cancellationToken);
}

public sealed class JewelryAdapter(ISafeHttpFetcher fetcher, IRobotsPolicy robots) : IJewelryAdapter
{
    public async Task<IReadOnlyList<JewelryQuote>> CollectAsync(
        JewelrySourceDefinition source, CancellationToken cancellationToken)
    {
        if (!source.AdapterAvailable || source.Endpoint is null)
            throw new InvalidOperationException("No verified adapter for this source.");
        var uri = new Uri(source.Endpoint);
        var response = await FetchAsync(uri, source.Id == "lukfook", cancellationToken);
        if (source.Id == "sge_daily")
        {
            // The official entry page points at its latest published trading date. Do not use today's date.
            using var document = new HtmlParser().ParseDocument(response.Text);
            if (document.QuerySelector("table.daily_new_table tbody tr") is null)
            {
                var link = document.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!)
                    .FirstOrDefault(h => Regex.IsMatch(h,
                        @"^/sjzx/quotation_daily_new\?start_date=\d{4}-\d{2}-\d{2}&end_date=\d{4}-\d{2}-\d{2}$",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
                if (link is null) throw new FormatException("SGE dated report link missing; page format changed.");
                response = await FetchAsync(new Uri(uri, link), false, cancellationToken);
            }
        }
        return JewelryParsers.Parse(source.Id, response.Text, response.FinalUri.AbsoluteUri);
    }

    private async Task<SafeHttpResponse> FetchAsync(Uri uri, bool mainland, CancellationToken cancellationToken)
    {
        await robots.EnsureAllowedAsync(uri, cancellationToken);
        var response = mainland
            ? await fetcher.FetchWithHeadersAsync(uri, 2 * 1024 * 1024,
                new Dictionary<string, string> { ["lang"] = "sc" }, cancellationToken)
            : await fetcher.FetchAsync(uri, 2 * 1024 * 1024, cancellationToken);
        if (response.FinalUri != uri) await robots.EnsureAllowedAsync(response.FinalUri, cancellationToken);
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"Source returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return response;
    }
}

public static class JewelryParsers
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<JewelryQuote> Parse(string sourceId, string text, string url) => sourceId switch
    {
        "sge_daily" => SgeDaily(text, url),
        "sge_benchmark" => SgeBenchmark(text, url),
        "gold_api" => GoldApi(text, url),
        "chowsangsang" => ChowSangSang(text, url),
        "chowtaifook" => ChowTaiFook(text, url),
        "lukfook" => Lukfook(text, url),
        "sojinjia" or "zhenshepin" or "jxgjtz" => BrandFallbackParsers.Parse(sourceId, text, url),
        _ => throw new FormatException("Unsupported source parser.")
    };

    private static IReadOnlyList<JewelryQuote> GoldApi(string text, string url)
    {
        using var document = JsonDocument.Parse(text);
        var data = document.RootElement;
        if (data.GetProperty("symbol").GetString() != "XAU" || data.GetProperty("currency").GetString() != "USD")
            throw new FormatException("Expected XAU in USD.");
        var timestamp = OffsetTimestamp(data.GetProperty("updatedAt").GetString()!);
        return [new JewelryQuote
        {
            InstrumentId = "XAU/USD", SourceId = "gold_api", SourceUrl = url, Kind = "spot",
            Region = "GLOBAL", PriceType = "indicative", Price = Number(data.GetProperty("price")),
            Currency = "USD", Unit = "troy_ounce", SourceTimestamp = timestamp,
            SourceDate = DateOnly.FromDateTime(timestamp.UtcDateTime), DateBasis = "source_timestamp_utc_date",
            Warnings = ["Provider indicative XAU/USD quote; underlying venue, bid/ask basis and delay are undisclosed.",
                "Unit follows XAU market convention (troy ounce); provider payload has no unit field."]
        }];
    }

    private static IReadOnlyList<JewelryQuote> ChowSangSang(string text, string url)
    {
        using var document = new HtmlParser().ParseDocument(text);
        var raw = document.All.Select(e => e.GetAttribute(":gold_data")).FirstOrDefault(v => v is not null)
            ?? throw new FormatException("Mainland gold_data attribute missing.");
        using var json = JsonDocument.Parse(raw);
        var result = new List<JewelryQuote>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            var mapping = type switch
            {
                "G_JW_SELL" => ("jewelry", "sell", "足金"),
                "G_INGOT_SELL" => ("bar", "sell", (string?)null),
                "G_JW_CNTPTBUY" => ("recycling", "buy_jewelry", "足金"),
                "G_INGOT_CNTPTBUY" => ("recycling", "buy_bar", (string?)null),
                _ => default
            };
            if (mapping.Item1 is null) continue;
            if (item.GetProperty("region").GetString() != "CHN" ||
                item.GetProperty("currencyCode").GetString() != "RMB" || item.GetProperty("weightUnit").GetString() != "GM")
                throw new FormatException("Unexpected region, currency or unit in mainland brand quote.");
            var effective = OffsetTimestamp(item.GetProperty("effectiveDate").GetString()!);
            result.Add(new JewelryQuote
            {
                InstrumentId = "BRAND:chowsangsang",
                SourceId = "chowsangsang",
                SourceUrl = url,
                Kind = "brand",
                Category = mapping.Item1,
                PriceType = mapping.Item2,
                Purity = mapping.Item3,
                Price = Number(item.GetProperty("price")),
                SourceDate = DateOnly.FromDateTime(effective.DateTime),
                SourceTimestamp = OffsetTimestamp(item.GetProperty("lastUpdateDate").GetString()!),
                RawProductName = item.GetProperty("description").GetString(),
                Applicability = mapping.Item1 == "recycling" ? "selected_stores_and_brand_products_only" : "mainland_reference_store_confirmation_required",
                Warnings = mapping.Item1 == "recycling"
                    ? ["Recovery service is provided by selected counterparties at designated stores, with product eligibility restrictions."]
                    : ["Labor fee inclusion is not specified in this feed; do not interpret as final transaction price."]
            });
        }
        return result;
    }

    private static IReadOnlyList<JewelryQuote> ChowTaiFook(string text, string url)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.GetProperty("code").GetInt32() != 200) throw new FormatException("CTF unsuccessful response.");
        var result = new List<JewelryQuote>();
        foreach (var group in document.RootElement.GetProperty("data").GetProperty("goldPriceDailyListVO").EnumerateArray())
            foreach (var item in group.GetProperty("goldPriceInfoListVO").EnumerateArray())
            {
                if (!item.GetProperty("isEnabled").GetBoolean() || !item.GetProperty("isShowInFrontend").GetBoolean()) continue;
                var name = item.GetProperty("displayName").GetString();
                var category = item.GetProperty("categoryGroup").GetInt32();
                var mapping = (category, name) switch
                {
                    (1, "足金") => ("jewelry", "sell", (string?)null),
                    (1, "工艺金章金条类") => ("bar", "sell", "craft_bar"),
                    (1, "投资黄金类") => ("bar", "sell", "investment_gold"),
                    (3, "黄金回收服务金价") => ("recycling", "buy", (string?)null),
                    _ => default
                };
                if (mapping.Item1 is null) continue;
                var date = DateTime.ParseExact(item.GetProperty("todayDate").GetString()!, "yyyy-MM-dd HH:mm:ss", Invariant);
                var updated = DateTime.ParseExact(item.GetProperty("inputTime").GetString()!, "yyyy-MM-dd HH:mm:ss", Invariant);
                result.Add(new JewelryQuote
                {
                    InstrumentId = "BRAND:chowtaifook",
                    SourceId = "chowtaifook",
                    SourceUrl = url,
                    Kind = "brand",
                    Category = mapping.Item1,
                    PriceType = mapping.Item2,
                    ProductVariant = mapping.Item3,
                    Price = Number(item.GetProperty("goldPrice")),
                    RawProductName = name,
                    Purity = name == "足金" ? "足金" : null,
                    SourceDate = DateOnly.FromDateTime(date),
                    SourceTimestamp = new DateTimeOffset(updated, TimeSpan.FromHours(8)),
                    Applicability = "official_mainland_mall_reference",
                    Warnings = ["Official mainland mall displays CNY/gram; some products charge additional labor fees.",
                    "Craft bars, investment gold and value-added/trade-in services are distinct; value-added service prices are excluded."]
                });
            }
        return result;
    }

    private static IReadOnlyList<JewelryQuote> Lukfook(string text, string url)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.GetProperty("status").GetInt32() != 1) throw new FormatException("Lukfook unsuccessful response.");
        var data = document.RootElement.GetProperty("data");
        var group = data.GetProperty("group");
        // Mainland is a six-group response. HK includes GoldB and eight groups in a different unit/currency.
        if (group.GetArrayLength() != 6 || group[0].TryGetProperty("GoldB", out _) ||
            !group[4].TryGetProperty("InvestmentS", out _) || !group[5].TryGetProperty("InvestmentPlatinumS", out _))
            throw new FormatException("Mainland Lukfook layout missing; refusing HK/default response.");
        var dateText = data.GetProperty("record_date").GetString()!;
        var timestamp = new DateTimeOffset(DateTime.ParseExact(dateText,
            ["yyyy-MM-dd H:mm:ss", "yyyy-MM-dd HH:mm:ss"], Invariant, DateTimeStyles.None), TimeSpan.FromHours(8));
        JewelryQuote Quote(string category, string type, decimal price, string product) => new()
        {
            InstrumentId = "BRAND:lukfook",
            SourceId = "lukfook",
            SourceUrl = url,
            Kind = "brand",
            Category = category,
            PriceType = type,
            Price = price,
            RawProductName = product,
            SourceDate = DateOnly.FromDateTime(timestamp.DateTime),
            SourceTimestamp = timestamp,
            DateBasis = "source_update_date",
            LaborFeeIncluded = category == "jewelry" ? "excluded" : null,
            Applicability = category == "recycling" ? "participating_stores_only" : "mainland_reference_store_confirmation_required",
            Warnings = ["Source page: https://www.lukfook.com/sc/page/goldprice; mainland request uses lang: sc.",
                "Purity is not a single explicit numeric grade in this feed; no purity normalization or automatic ranking."]
        };
        return
        [
            Quote("jewelry", "sell", Number(group[0].GetProperty("GoldS")), "足金999/足金"),
            Quote("bar", "sell", Number(group[4].GetProperty("InvestmentS")), "黄金添富金金章金条"),
            Quote("recycling", "buy_bar", Number(group[4].GetProperty("InvestmentB")), "黄金添富金金章金条（部分门店提供回收）")
        ];
    }

    private static IReadOnlyList<JewelryQuote> SgeDaily(string text, string url)
    {
        using var document = new HtmlParser().ParseDocument(text);
        var table = document.QuerySelector("table.daily_new_table") ?? throw new FormatException("SGE daily table missing.");
        var headers = table.QuerySelectorAll("thead th").Select(x => x.TextContent.Trim()).ToArray();
        int Index(string header) => Array.FindIndex(headers, h => h == header);
        var dateIndex = Array.FindIndex(headers, h => h.Contains("日期", StringComparison.Ordinal));
        var instrumentIndex = Array.FindIndex(headers, h => h.Contains("合约", StringComparison.Ordinal));
        var closeIndex = Index("收盘价");
        if (dateIndex < 0 || instrumentIndex < 0 || closeIndex < 0) throw new FormatException("SGE daily headers changed.");
        var result = new List<JewelryQuote>();
        foreach (var row in table.QuerySelectorAll("tbody tr"))
        {
            var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray();
            if (cells.Length != headers.Length) throw new FormatException("SGE daily row/header mismatch.");
            var instrument = cells[instrumentIndex];
            if (instrument is not ("Au99.99" or "Au(T+D)")) continue;
            if (cells[closeIndex] is "-" or "" || cells[closeIndex] == "0.0" || cells[closeIndex] == "0.00") continue;
            result.Add(new JewelryQuote
            {
                InstrumentId = "SGE:" + instrument,
                SourceId = "sge_daily",
                SourceUrl = url,
                Kind = instrument == "Au99.99" ? "spot" : "deferred",
                PriceType = "close",
                Price = Positive(cells[closeIndex]),
                Purity = instrument == "Au99.99" ? "9999" : null,
                SourceDate = DateOnly.ParseExact(cells[dateIndex], "yyyy-MM-dd", Invariant),
                DateBasis = "exchange_trade_date",
                HistoryKind = "official_daily",
                MarketSession = "daily_report",
                Warnings = ["Official daily close; no intraday timestamp or real-time quote. Night session belongs to the exchange trading date."]
            });
        }
        return result;
    }

    private static IReadOnlyList<JewelryQuote> SgeBenchmark(string text, string url)
    {
        // The endpoint is an HTML table fragment. Wrapping it preserves rows in an HTML5 parser.
        using var document = new HtmlParser().ParseDocument("<table>" + text + "</table>");
        var headers = document.QuerySelectorAll("th").Select(c => c.TextContent.Trim()).ToArray();
        if (!headers.SequenceEqual(new[] { "交易日", "合约", "早盘价", "午盘价" }))
            throw new FormatException("Expected final AM/PM benchmark headers, not auction rounds.");
        var result = new List<JewelryQuote>();
        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray();
            if (cells.Length == 0) continue;
            if (cells.Length != 4 || cells[1] != "SHAU") throw new FormatException("Unexpected benchmark contract or row.");
            for (var i = 2; i <= 3; i++)
            {
                if (cells[i] is "" or "-" or "0" or "0.00" or "0.0") continue;
                result.Add(new JewelryQuote
                {
                    InstrumentId = "SGE:SHAU",
                    SourceId = "sge_benchmark",
                    SourceUrl = url,
                    Kind = "benchmark",
                    PriceType = i == 2 ? "benchmark_am" : "benchmark_pm",
                    Price = Positive(cells[i]),
                    SourceDate = DateOnly.ParseExact(cells[0], "yyyyMMdd", Invariant),
                    DateBasis = "exchange_trade_date",
                    HistoryKind = "official_daily",
                    MarketSession = "daily_report",
                    Warnings = ["Official fixed-session benchmark; not a continuous spot quote."]
                });
            }
        }
        return result;
    }

    private static decimal Number(JsonElement value) => Positive(value.ValueKind == JsonValueKind.String
        ? value.GetString()! : value.GetRawText());
    private static decimal Positive(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                Invariant, out var price) || price <= 0)
            throw new FormatException("Quote must be a positive decimal; no zero/placeholder prices.");
        return price;
    }
    private static DateTimeOffset OffsetTimestamp(string value)
    {
        if (!Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new FormatException("Source timestamp must include an explicit offset.");
        return DateTimeOffset.Parse(value, Invariant, DateTimeStyles.None);
    }
}
