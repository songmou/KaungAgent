namespace KaungMcp.Jewelry;

public static class JewelryCatalog
{
    public static readonly JewelrySourceDefinition[] Sources =
    [
        new("sge_daily", "上海黄金交易所每日行情", "https://www.sge.com.cn/sjzx/quotation_daily_new",
            "https://www.sge.com.cn/sjzx/quotation_daily_new", "verified_sample", "2026-09-27: official dated rows retrieved; daily close, not live; seven-day observation pending.", 15, true),
        new("sge_benchmark", "上海金基准价", "https://www.sge.com.cn/sjzx/jzj",
            "https://www.sge.com.cn/sjzx/everjzj", "verified_sample", "2026-09-27: official AM/PM benchmark table; not auction rounds.", 15, true),
        new("gold_api", "Gold API XAU/USD", "https://gold-api.com/docs",
            "https://api.gold-api.com/price/XAU", "verified_sample", "2026-09-27: repeated public JSON responses with updatedAt; provider composite quote, underlying venue/delay not disclosed. https://gold-api.com/terms", 15, true),
        new("cme", "CME COMEX 黄金", "https://www.cmegroup.com/markets/metals/precious/gold.quotes.html",
            null, "unavailable", "2026-09-27: HTTP 403. No validated adapter or concrete contract catalog; disabled. Public quotes delayed at least 10 minutes.", 15, false),
        new("chowtaifook", "周大福", "https://www.ctfmall.com/todaygold",
            "https://api2.ctfmall.com/gateway/ctfmall-common2-server/common/ctfTodayGoldPriceNew",
            "access_pending", "2026-09-27: official mall public JSON retrieved twice and parser validated, but api2.ctfmall.com/robots.txt returned HTTP 502 twice. Disabled until access-policy verification succeeds; no bypass.", 60, true),
        new("chowsangsang", "周生生", "https://cn.chowsangsang.com/gold-info",
            "https://cn.chowsangsang.com/gold-info", "verified_sample", "2026-09-27: repeated mainland gold_data JSON, effectiveDate and lastUpdateDate verified.", 60, true),
        new("lukfook", "六福", "https://www.lukfook.com/sc/page/goldprice",
            "https://www.lukfook.com/api/goldprice/page", "verified_sample", "2026-09-27: repeated mainland JSON using lang: sc; default response is HK and must be rejected.", 60, true),
        new("laofengxiang", "老凤祥", "https://www.laofengxiang.com/", null,
            "unavailable", "2026-09-27: official site displays a maintenance notice; no validated dated quote source.", 60, false),
        new("laomiao", "老庙", "https://www.laomiao.com.cn/", null,
            "unavailable", "2026-09-27: TLS connection failed; no validated quote source.", 60, false),
        new("chowtaiseng", "周大生", "https://www.chowtaiseng.com/", null,
            "pending", "2026-09-27: homepage retrieved, no validated dated quote source.", 60, false),
        new("chinagold", "中国黄金", "https://www.chinagoldgroup.com/", null,
            "pending", "2026-09-27: group homepage retrieved; retail quote scope and source still unverified.", 60, false),
        new("caibai", "菜百", "https://www.bjcaibai.com.cn/", null,
            "pending", "2026-09-27: dynamic official homepage; dated price feed not validated.", 60, false),
        new("sojinjia", "搜金价品牌汇总", "https://www.sojinjia.cn/pinpai", "https://www.sojinjia.cn/pinpai",
            "verified_sample", "2026-10-09: public mainland dated cards and robots allow verified; only gold rows parsed.", 60, true),
        new("cngolddaily", "今日金价网品牌汇总", "https://cngolddaily.com/brands", "https://cngolddaily.com/brands",
            "pending", "2026-10-09: HTTP 200; robots 404 permits access, but prices are calculated premiums without per-row dates. Not eligible for dated brand quotes.", 60, false),
        new("zhenshepin", "珍奢品品牌汇总", "https://www.zhenshepin.com/pinpaijinjia/", "https://www.zhenshepin.com/pinpaijinjia/",
            "verified_sample", "2026-10-09: public per-row dates, CNY/gram gold products and robots allow verified.", 60, true),
        new("jxgjtz", "品牌金价汇总", "https://jin.jxgjtz.com/pinpaijinjia/", "https://jin.jxgjtz.com/pinpaijinjia/",
            "verified_sample", "2026-10-09: public gold-only table columns, per-row mainland timestamps and robots allow verified.", 60, true)
    ];

    public static readonly JewelryInstrument[] Instruments =
    [
        new("SGE:Au99.99", "上金所 Au99.99", "spot", "sge_daily", "CN", ["market"]),
        new("SGE:Au(T+D)", "上金所 Au(T+D)", "deferred", "sge_daily", "CN", ["market"]),
        new("SGE:SHAU", "上海金基准价（早盘/午盘）", "benchmark", "sge_benchmark", "CN", ["market"]),
        new("XAU/USD", "国际现货黄金（Gold API 报价）", "spot", "gold_api", "GLOBAL", ["market"]),
        // A family is discoverable, but never presented as a priced or continuous contract.
        new("COMEX:GC", "COMEX 黄金合约目录待接入", "futures_family", "cme", "US", ["market"]),
        .. JewelryBrands.All.Select(b => new JewelryInstrument(
            b.InstrumentId, b.Name, "brand", b.Id, "CN", ["jewelry", "bar", "recycling"]))
    ];
}
