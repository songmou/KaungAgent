using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace KaungMcp.Jewelry;

public static class BrandFallbackParsers
{
    public static IReadOnlyList<JewelryQuote> Parse(string sourceId, string text, string url)
    {
        using var doc = new HtmlParser().ParseDocument(text);
        var quotes = new List<JewelryQuote>();
        var recognized = 0;
        if (sourceId == "sojinjia")
        {
            foreach (var card in doc.QuerySelectorAll("a[href^='/pinpai/']"))
            {
                var spans = card.QuerySelectorAll("span").Select(s => s.TextContent.Trim()).ToArray();
                if (spans.Length < 5) continue;
                recognized++;
                if (!card.TextContent.Contains("元/克", StringComparison.Ordinal)) continue;
                Add(spans[0], spans[1], spans[3], spans[2]);
            }
        }
        else if (sourceId is "zhenshepin" or "jxgjtz")
        {
            foreach (var table in doc.QuerySelectorAll("table"))
            {
                var headers = table.QuerySelectorAll("th").Select(c => c.TextContent.Trim()).ToArray();
                if (headers.Length == 0 || headers[0] != "品牌" || !headers.Any(h => h.Contains("时间", StringComparison.Ordinal))) continue;
                foreach (var row in table.QuerySelectorAll("tr"))
                {
                    var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray();
                    if (cells.Length != headers.Length) continue;
                    recognized++;
                    // jxgjtz's fourth/fifth columns are platinum, never parse them as gold.
                    Add(cells[0], cells[1], cells[2], cells[^1]);
                }
            }
        }
        else throw new FormatException("Unverified brand parser.");
        if (recognized == 0) throw new FormatException("Brand quote layout changed or no dated rows found.");
        return quotes.GroupBy(q => q.SeriesKey + "|" + q.SourceDate).Select(g => g.First()).ToArray();

        void Add(string brandName, string product, string amount, string dateText)
        {
            var brand = JewelryBrands.Find(brandName);
            if (brand is null || product.Contains("铂", StringComparison.Ordinal) || product.Contains("银", StringComparison.Ordinal) ||
                product.Contains("钯", StringComparison.Ordinal) || product.Contains("换", StringComparison.Ordinal) ||
                product.Contains("K金", StringComparison.OrdinalIgnoreCase)) return;
            var mapping = Product(product);
            if (mapping is null) return;
            var match = Regex.Match(amount, @"^([0-9][0-9,]*(?:\.[0-9]+)?)(?:元/克)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0) return;
            var dated = Regex.Match(dateText, @"^\d{4}-\d{1,2}-\d{1,2}(?: \d{2}:\d{2}:\d{2})?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!dated.Success || !DateTime.TryParseExact(dateText,
                ["yyyy-M-d", "yyyy-MM-dd", "yyyy-M-d HH:mm:ss", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return;
            var (category, variant, type, purity) = mapping.Value;
            quotes.Add(new JewelryQuote
            {
                InstrumentId = brand.InstrumentId, SourceId = sourceId, SourceUrl = url, Kind = "brand", Region = "CN",
                Category = category, ProductVariant = variant, PriceType = type, Purity = purity,
                Price = price, Currency = "CNY", Unit = "gram", RawProductName = product,
                SourceDate = DateOnly.FromDateTime(date), SourceTimestamp = dateText.Contains(' ') ? new DateTimeOffset(date, TimeSpan.FromHours(8)) : null,
                Applicability = "mainland_reference_store_confirmation_required",
                Warnings = ["Third-party brand reference; labor fees and store applicability require confirmation."]
            });
        }
    }

    private static (string Category, string? Variant, string Type, string? Purity)? Product(string product) => product switch
    {
        "黄金价格" or "黄金饰品" or "首饰黄金价格" or "黄金首饰价格" => ("jewelry", null, "sell", null),
        "足金饰品" or "足金价格" or "千足金饰品" or "千足金首饰" => ("jewelry", null, "sell", "足金"),
        "999黄金价格" or "足金999价格" => ("jewelry", "gold_999", "sell", "999"),
        "工艺金价格" or "工艺品黄金价格" => ("jewelry", "craft_gold", "sell", null),
        "金条" or "金条价格" or "足金金条" or "投资类黄金价格" or "投资黄金价格" or "投资金条价格" => ("bar", "investment_gold", "sell", null),
        "工艺金条价格" or "饰品金条价格" => ("bar", "craft_bar", "sell", null),
        "黄金回收价格" => ("recycling", null, "buy", null),
        "金条回购价格" => ("recycling", null, "buy_bar", null),
        _ => null
    };
}
