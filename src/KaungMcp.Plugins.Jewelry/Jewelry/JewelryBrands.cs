namespace KaungMcp.Jewelry;

public sealed record JewelryBrand(string Id, string Name, string[] Aliases)
{
    public string InstrumentId => "BRAND:" + Id;
}

public static class JewelryBrands
{
    // IDs of the original eight brands are stable; aliases are explicitly reviewed.
    public static readonly JewelryBrand[] All =
    [
        new("chowtaifook", "周大福", []),
        new("chowsangsang", "周生生", []),
        new("lukfook", "六福", ["六福珠宝"]),
        new("laofengxiang", "老凤祥", []),
        new("laomiao", "老庙", ["老庙黄金"]),
        new("chowtaiseng", "周大生", []),
        new("chinagold", "中国黄金", []),
        new("caibai", "菜百", ["菜百首饰", "菜百黄金"]),
        new("chowliufook", "周六福", []),
        new("chowtaifookjewelry", "潮宏基", []),
        new("mengjinyuan", "梦金园", []),
        new("kingfook", "金至尊", []),
        new("ming", "明牌珠宝", []),
        new("cuihua", "萃华金店", []),
        new("yayi", "亚一金店", []),
        new("chenghuang", "城隍珠宝", []),
        new("qilu", "齐鲁金店", []),
        new("taiyang", "太阳金店", []),
        new("dongxiang", "东祥金店", []),
        new("changzhou", "常州金店", []),
        new("yangzhou", "扬州金店", []),
        new("baitai", "百泰黄金", []),
        new("jimeng", "吉盟珠宝", []),
        new("jiahua", "嘉华珠宝", []),
        new("fuyi", "富艺珠宝", []),
        new("qianxi", "千禧之星", []),
        new("xingguangda", "星光达珠宝", []),
        new("duobian", "多边金都珠宝", []),
        new("kingtai", "金大福", []),
        new("jinxiang", "金象珠宝", []),
        new("jinlan", "金兰首饰", []),
        new("laiyin", "莱音珠宝", []),
        new("futai", "福泰珠宝", []),
        new("pangdonglai", "胖东来珠宝", []),
        new("gaosai", "高赛尔", []),
        new("jinyinjie", "金银街", []),
        new("shuibei", "水贝黄金", ["水贝"]),
        new("baoqing", "宝庆银楼", []),
        new("tsl", "谢瑞麟", []),
    ];
    public static readonly string[] FallbackSources = ["sojinjia", "cngolddaily", "zhenshepin", "jxgjtz"];
    public static bool IsFallback(string sourceId) => FallbackSources.Contains(sourceId);
    public static JewelryBrand? Find(string name) => All.SingleOrDefault(b => b.Name == name.Trim() || b.Aliases.Contains(name.Trim()));
    public static bool Supports(string instrumentId, string sourceId) => IsFallback(sourceId)
        ? All.Any(b => b.InstrumentId == instrumentId)
        : instrumentId == "BRAND:" + sourceId;
    public static string[] CollectionSources => JewelryCatalog.Sources.Where(s => s.MinimumIntervalMinutes == 60).Select(s => s.Id).ToArray();
    public static string Slot(JewelryQuote q) => string.Join('|', q.InstrumentId, q.Category,
        q.ProductVariant ?? (q.Category == "bar" ? "investment_gold" : "standard"), q.PriceType);
    public static int Priority(string sourceId) => IsFallback(sourceId) ? Array.IndexOf(FallbackSources, sourceId) + 1 : 0;
}
