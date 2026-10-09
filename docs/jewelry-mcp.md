# 珠宝行情 MCP

在现有 KaungMcp 的 `/mcp` 端点中增加七个只读工具，使用现有客户端 API Key、调用日志和结构化返回。不需要另建 MCP 服务；现有 Agent 重新“连接并发现”即可看到新工具。首次启动自动执行增量迁移，保留原客户端和日志。

## 当前交付与验收边界

本模块是公开行情数据首版，面向开发者和 Agent。**完成工具和存储能力不等于完成所有数据覆盖验收。**

2026-09-27 已使用真实采集链（robots、安全 HTTP、解析、SQLite、查询）在临时数据库验证五个可启用来源：上金所日行情、上海金基准价、Gold API、周生生、六福。只完成当次样本验证，尚未完成连续七天运行观察。COMEX 具体月份合约尚未接入，第三个品牌周大福因访问策略验证失败而禁用，因此原计划的完整覆盖验收仍未达成。

| 来源 ID | 品种 / 品牌 | 当前状态 | 说明 |
|---|---|---|---|
| `sge_daily` | Au99.99、Au(T+D) | 样本验证通过、启用 | 官方每日收盘价；按官方入口的最新报告日期读取，不是即时行情 |
| `sge_benchmark` | 上海金 SHAU | 样本验证通过、启用 | 正式早盘/午盘基准价；不将竞价轮次当成最终基准 |
| `gold_api` | XAU/USD | 样本验证通过、启用 | 独立提供商指示性报价，保留更新时间；底层交易场所、买卖盘口径和延时未披露 |
| `cme` | COMEX 黄金 | 不可用、禁用 | 官方报价页 HTTP 403，延时页/robots 请求超时；不绕过访问限制、不使用连续合约替代 |
| `chowsangsang` | 周生生 | 样本验证通过、启用 | 官网内地页面中的结构化 `gold_data`，包含生效日期和源更新时间 |
| `lukfook` | 六福 | 样本验证通过、启用 | 官方 API 必须发送 `lang: sc`；默认香港响应会被拒绝 |
| `chowtaifook` | 周大福 | 访问验证待完成、禁用 | 官方商城报价接口和解析样本已验证；API 主机 robots 两次 HTTP 502，现有访问策略拒绝采集 |
| `laofengxiang` | 老凤祥 | 不可用、禁用 | 官网返回维护公告 |
| `laomiao` | 老庙 | 不可用、禁用 | 官网 TLS 连接失败 |
| `chowtaiseng` | 周大生 | 待验证、禁用 | 官网可读取，尚未验证有日期的价格来源 |
| `chinagold` | 中国黄金 | 待验证、禁用 | 已查集团官网，零售报价来源和适用范围尚未验证 |
| `caibai` | 菜百 | 待验证、禁用 | 官网为动态页面，尚未验证有日期的价格接口 |

来源链接、接口地址、验证证据和运行状态均可通过 `kaung_jewelry_get_source_status` 获取。`verified_sample` 只说明接入样本已验证，不代表实时、授权再分发承诺或持续稳定。目录中的 `categories` 是计划查询范围，不能据此推断所有品牌都已提供所有类别。

## 工具与参数

| 工具 | 主要参数 | 返回 |
|---|---|---|
| `kaung_jewelry_list_instruments` | 无 | 精确品种 ID、地区、类别、来源启用和验证状态 |
| `kaung_jewelry_get_market_quotes` | `instrumentIds?`, `date="latest"` | 按品种返回报价、日期缺失和最近可得数据 |
| `kaung_jewelry_get_brand_quotes` | `brandIds?`, `date="latest"`, `category?`, `region="CN"` | 八品牌批量查询，逐项允许失败 |
| `kaung_jewelry_get_price_history` | `instrumentId`, `startDate`, `endDate`, `dateBasis="source_date"`, `offset=0`, `limit=100` | 已存历史观察记录；最大 367 天范围，每页最多 500 条 |
| `kaung_jewelry_compare_prices` | `quoteIds`, `mode="rank"` | 2–20 条存储报价的价差与百分比 |
| `kaung_jewelry_convert_price` | `price`, `fromCurrency`, `fromUnit`, `toCurrency`, `toUnit`, `exchangeRate?`, `exchangeRateSource?`, `exchangeRateTimestamp?`, `decimals=4` | 原始输入、换算结果、汇率出处、时间及公式 |
| `kaung_jewelry_get_source_status` | 无 | 验证证据、启用状态、最近尝试/成功、下次尝试、连续失败数、近七天尝试与成功数 |

品种 ID：`SGE:Au99.99`、`SGE:Au(T+D)`、`SGE:SHAU`、`XAU/USD`。`COMEX:GC` 仅为不可用合约族占位，不是主力合约或有价格的连续合约。

品牌 ID 为 `BRAND:` 加来源 ID，如 `BRAND:chowsangsang`、`BRAND:lukfook`。类别为 `jewelry`（首饰）、`bar`（金条/金章）、`recycling`（回收）。工艺金条与投资黄金在有数据时以 `productVariant` 分开，不合并价格序列。换购/增值服务不当成回收价。

### 日期、新鲜度和历史

- `date` 接受 `latest`、`today` 或 `yyyy-MM-dd`。`today` 是 Asia/Shanghai 的日历日期，匹配各来源的 `sourceDate`；国际现货的源日期来自 UTC 时间，跨时区时可能与上海日期不同，返回 `dateBasis` 提示。
- 指定日期无数据：`status=date_missing`，`quotes=[]`，最近可得记录单独放在 `latestAvailable`，不冒充指定日价格。
- 最近刷新失败、返回空数据或来源已停用但仍有存储报价时，返回 `status=cached_fallback` 并在 `reason` 解释原因，不静默掩盖采集故障。
- `sourceTimestamp` 是来源标明的更新时间，缺失时为 `null`。日度报告只有交易日，不造一个盘中时间；`collectedAt` 独立记录抓取时间。
- `dataState` 有 `current`、`date_only`、`prior_date`、`source_stale`、`unknown_time`、`stale_cache`。缓存超过采集间隔两倍标记陈旧；这只是新鲜度提示，不是交易日历判定。
- 不推测节假日和国际市场是否开市。日度报告标 `marketSession=daily_report`，其余无法核实则 `unknown`。周末旧报价不会改成今日，休市零价不入库。
- `historyKind=official_daily` 表示官方日度数据，`collected_snapshot` 表示采集时观察到的数据。每次成功采集保存快照，可能有同一源报价的重复观察；无自动历史回填、补点或合成 K 线。
- 历史默认按来源日期筛选；未知来源日期可以通过 `dateBasis=collected_date` 查询 UTC 采集日期。按采集时间和 ID 分页，返回 `nextOffset`。
- 涨跌额、涨跌幅和基准字段已预留；当前来源尚未核实一致计算口径，保持 `null`，不从不同时间或品种价格拼算。

### 比较与换算

`rank` 要求同一来源日期、同一日期含义、品牌、地区、类别、产品变体、明确且相同的纯度/工费口径和适用范围，每品牌一条。排序按价格从低到高，差额相对于最低报价。**未知工费或纯度时会拒绝排名**；当前公开源不一定满足严格排名条件。

`reference` 按输入顺序给出相对于第一条报价的数值差额，允许不同产品/品牌与大盘做参考，但必须相同币种、单位且来源日期明确一致；不同日期含义或更新时间会附警告。不称作利润、零售毛利或套利机会。

换算不改动已存报价，也不会将换算后的数字伪造为官方报价。跨币种必须由调用方提供汇率、来源和带时区的 ISO 时间。汇率方向是 **1 单位源币种对应多少目标币种**。

`目标价格 = 原价 / 原单位克数 × 目标单位克数 × 汇率`，`1 troy_ounce = 31.1034768 gram`。只接受克和金衡盎司；金额使用 `decimal`，小数位 0–8，采用 AwayFromZero 中点舍入。不隐含税费、纯度或溢价调整。Gold API 响应没有单位字段，按 XAU 的金衡盎司市场惯例归一化，并在每条报价中明确这一点。

### 调用示例

下面是 `tools/call` 的 `name` 与 `arguments` 示例，使用现有 MCP 客户端连接即可。

```json
{"name":"kaung_jewelry_get_market_quotes","arguments":{"instrumentIds":["SGE:Au99.99","XAU/USD","COMEX:GC"],"date":"latest"}}
```

```json
{"name":"kaung_jewelry_get_brand_quotes","arguments":{"brandIds":["BRAND:chowsangsang","BRAND:lukfook","BRAND:chowtaifook"],"date":"today","category":"jewelry"}}
```

```json
{"name":"kaung_jewelry_get_price_history","arguments":{"instrumentId":"SGE:Au99.99","startDate":"2026-09-01","endDate":"2026-09-27","limit":100}}
```

以下价格和汇率只是数学演示，不是当日行情：

```json
{"name":"kaung_jewelry_convert_price","arguments":{"price":3110.34768,"fromCurrency":"USD","fromUnit":"troy_ounce","toCurrency":"CNY","toUnit":"gram","exchangeRate":7,"exchangeRateSource":"caller-provided-example","exchangeRateTimestamp":"2026-09-27T10:00:00+08:00"}}
```

结果为 `convertedPrice=700`，附原价、汇率时间和公式。比较时先查价，从 `quote.id` 取真实 ID，然后传给 `quoteIds`，不让模型构造 UUID。

日期缺失的结构示意（省略最近报价内容）：

```json
{
  "requestedDate": "2026-09-27",
  "items": [{
    "instrumentId": "BRAND:lukfook",
    "status": "date_missing",
    "quotes": [],
    "latestAvailable": [],
    "reason": "No observed quote with this source date/category. LatestAvailable is not a substitute."
  }]
}
```

实际 `latestAvailable` 会在有旧报价时携带完整的 `quote`、`dataState` 和 `cacheStale`。

Agent 示例：“先发现支持的品牌，再查询今日首饰报价；把未更新和不可用单列。若纯度/工费不明，不给最低价排名。国际报价若换算人民币/克，先向调用方取得汇率及出处。”

## 配置与采集

`appsettings.json` 的 `Jewelry` 段，或等价环境变量：

```json
{
  "Jewelry": {
    "Enabled": true,
    "BackgroundCollection": true,
    "MarketIntervalMinutes": 15,
    "BrandIntervalMinutes": 60,
    "TimeoutSeconds": 20,
    "Sources": { "lukfook": true, "gold_api": true }
  }
}
```

- 环境变量如 `Jewelry__Sources__gold_api=false`。`BackgroundCollection`、`MarketIntervalMinutes`、`BrandIntervalMinutes` 仅用于首次生成默认任务；此后通过 [定时任务管理页](scheduler.md) 调整执行计划，保存后立即生效。
- `Enabled=false` 停止采集，已存历史仍可查询。默认任务按国际金、上海金、品牌金三组创建；停机期间错过的计划时间不逐次补跑。
- 大盘来源最低十五分钟、品牌最低一小时；后台每分钟检查到期任务。查询在无缓存或任务逾期时最多触发一次补采，失败来源仍遵守退避时间。
- 未通过来源验证的条目不会因 `Sources` 配置写 `true` 而启用。需先验证访问和解析，再更新来源登记状态；CME 还需要新增实际月份合约目录及适配器。
- 新缓存直接返回；无缓存或到期时，单来源最多二十秒（可配 1–60 秒）刷新。多来源并行，同来源并发请求合并；失败保留旧报价并标出缓存年龄。
- 失败指数退避，上限一天，成功后恢复正常间隔；空数据记 `no_data`，不保存占位价。故障不影响其他来源。
- HTTP 复用现有 SSRF/DNS/响应大小/重定向保护和 robots 策略。六福公开地区头只发往原始主机，不跨主机重定向转发。不跳过 TLS、登录、验证码或 robots 错误。
- 数据表：`JewelrySources`、`JewelryCollectionStates`、`JewelryCollectionAttempts`、`JewelryQuotes`。金额在规范化 JSON 中按十进制保存，索引字段用于品种、来源日期和采集时间查询。单次采集的数据和状态原子写入。
- SQLite 面向现有单进程本地服务；同一数据库不支持多个独立采集进程协调。观察期间用现有 MCP 工具查看状态，不并行运行额外采集进程。

一次性诊断：`dotnet run --project src/KaungMcp -- --jewelry-probe`。使用配置的数据库，遵守持久化间隔，打印来源状态和当前报价后退出，不启动 HTTP 服务。使用前按项目 README 配置管理员密钥。若只想测试，设置独立的 `ConnectionStrings__KaungMcp` 和 `McpManagement__SettingsPath`；不要用临时配置覆盖常用库。

## 七天观察与后续验收

保持 KaungMcp 运行七天，按源记录每次成功、失败、耗时起止、报价数量和下一次尝试。通过 `get_source_status` 查看近七天次数与连续失败；`seven_calendar_dates_recorded_review_required` 只表示覆盖了七个 UTC 日期，**不代表已持续运行 168 小时或通过稳定性验收**。结合尝试记录的完整时间范围、每天覆盖、错误率和源报价日期人工确认。

完整验收仍需：CME 至少一个明确月份合约可用、周大福或其他第三品牌通过访问验证并完成七天观察、来源更新规律符合声明。当前不将这些未完成条件标记为通过。

后续可扩展白银/铂金/钯金、自动汇率、异动查询和通知。首版不包含交易、预测、门店业务、自动提醒、固定时点 LBMA 数据或付费接口。
