# Jewelry parser fixtures

Extracted on 2026-09-27 from public provider responses. These are historical test facts, not runtime fallback data. HTML fixtures retain only pricing rows or the pricing JSON attribute. No cookies, user accounts or page tokens are stored.

- sge-daily.html: https://www.sge.com.cn/sjzx/quotation_daily_new?start_date=2026-09-24&end_date=2026-09-24
- sge-benchmark.html: https://www.sge.com.cn/sjzx/everjzj
- gold-api.json: https://api.gold-api.com/price/XAU
- chowsangsang.html: https://cn.chowsangsang.com/gold-info
- lukfook-cn.json: https://www.lukfook.com/api/goldprice/page with `lang: sc`
- lukfook-hk.json: same endpoint without `lang: sc`; deliberately rejected by mainland parser
- chowtaifook.json: https://api2.ctfmall.com/gateway/ctfmall-common2-server/common/ctfTodayGoldPriceNew (source remains disabled because robots verification failed)

These files are used only by offline tests, never loaded into the live database.

2026-10-09：sojinjia.html、zhenshepin.html、jxgjtz.html 是公开页面，验证大陆黄金及逐条日期。cngolddaily.html 是无逐条日期的计算报价反例，保持禁用。采集 User-Agent 为 KaungMcpBot；前三者 robots 允许路径，cngolddaily robots 404。样本不代表七天稳定性验收。
