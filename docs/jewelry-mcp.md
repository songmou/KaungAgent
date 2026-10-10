# 珠宝行情 MCP

KaungMcp `/mcp` 对客户端提供六个只读金价工具，使用客户端 Bearer API Key、调用日志和结构化返回。更新后 Agent 需要重新“连接并发现”，刷新已保存的能力 Schema。来源状态工具仅供管理员内部测试。

## 对外接口

| 工具 | 主要参数 | 返回 |
|---|---|---|
| `kaung_jewelry_list_instruments` | 无 | 品种 ID、名称、地区、类别 |
| `kaung_jewelry_get_market_quotes` | `instrumentIds?`, `date="latest"` | 国内与国际报价、日期与新鲜度 |
| `kaung_jewelry_get_brand_quotes` | `brandIds?`, `date="latest"`, `category?`, `region="CN"` | 大陆黄金品牌报价，品牌逐项允许缺失 |
| `kaung_jewelry_get_price_history` | `instrumentId`, `startDate`, `endDate`, `dateBasis="source_date"`, `offset=0`, `limit=100` | 已存报价记录，最大 367 天，每页最多 500 条 |
| `kaung_jewelry_compare_prices` | `quoteIds`, `mode="rank"` | 2–20 条记录的价差与百分比 |
| `kaung_jewelry_convert_price` | `price`, `fromCurrency`, `fromUnit`, `toCurrency`, `toUnit`, `exchangeRate?`, `exchangeRateSource?`, `exchangeRateTimestamp?`, `decimals=4` | 输入金额、换算结果、汇率、时间与公式 |

品牌查询无 `sourceId` 参数。公开返回模型只允许品种、品牌、产品类别/变体、纯度、价格、币种、单位、报价日期、更新时间、采集时间和新鲜度等数据。目录不返回来源数组；报价和历史不返回采集网站、来源标识、网址、原始产品文本、验证证据或抓取错误。比较输出不附带来源信息，换算不会回显调用方的汇率出处。

这是一次返回结构调整：`quote.sourceDate` 改为 `quote.quoteDate`，`quote.sourceTimestamp` 改为 `quote.quoteTimestamp`，删除目录 `sources` 和品种 `sourceId`；报价记录 `id` 保留，继续用于比较。历史查询入参 `dateBasis=source_date` 保持兼容，含义为按报价日期筛选。

品种 ID 保留 `SGE:Au99.99`、`SGE:Au(T+D)`、`SGE:SHAU`、`XAU/USD`。`COMEX:GC` 是暂不可用的合约族占位，不代表连续合约价格。原八品牌 ID 不变；扩展后的品牌从目录获取，不让 Agent 猜测 ID。最多按 ID 批量查询 20 个，省略 ID 查询全部支持品牌。

类别为 `jewelry`、`bar`、`recycling`。工艺金条和投资金条在有明确信息时以 `productVariant` 分开；换购不视为回收。公开的产品名称由本地类别规范化，原始网页产品名称仅内部保存。未知工费、纯度不推断。

### 日期、历史与比较

- `date` 支持 `latest`、`today`、`yyyy-MM-dd`。`today` 使用北京时间日历日期，国际行情报价日期依其 UTC 时间；报价时间不会用采集时间替代。
- 指定日期缺失时 `quotes=[]`、`status=date_missing`，旧报价只在 `latestAvailable` 单列。对外缺失提示为“当日未更新”或“暂无报价”，不包含内部错误。
- `dataState` 标记 `current`、`date_only`、`prior_date`、`source_stale`、`unknown_time` 或 `stale_cache`；`source_stale` 是保留的新鲜度状态枚举，不含具体来源信息。`cacheStale` 表示采集缓存过期。
- 同日同品类优先官网，其次按补齐网站顺序选择；官网后来更新时改为展示官网数据，原补齐历史仍可查询。
- 历史仅包含已保存观察，不补点、不造收盘价；按报价日期或 UTC 采集日期筛选，按采集时间及 ID 分页，返回 `nextOffset`。
- `rank` 需要同日、同品类、地区、产品变体及明确一致的纯度/工费/适用范围，每品牌一条。未知工费或纯度时拒绝排名。`reference` 只返回数值差额，要求同币种、单位且报价日期一致，不解释为利润。
- 只支持克及金衡盎司，`1 troy_ounce = 31.1034768 gram`。跨币种必须显式提供汇率、出处和带时区时间，出处仅用于输入校验，不返回。金额用 decimal，小数位 0–8，AwayFromZero 舍入；不自动获取汇率或调整税费、纯度。

调用示例：

```json
{"name":"kaung_jewelry_get_brand_quotes","arguments":{"brandIds":["BRAND:chowtaifook","BRAND:laofengxiang"],"date":"today","category":"jewelry"}}
```

Agent 提示：“先发现品牌，再查询今日所需品类。将当日缺失、旧报价与缓存过期单列，不把最近报价冒充今日报价。用 quoteDate、quoteTimestamp、collectedAt 判断时间；未知纯度或工费不排名。比较使用实际返回的 quote.id。国际报价换算人民币/克时要求调用方提供汇率，不自动猜测。公开结果不提供采集来源。”

## 内部采集与保存

品牌和采集网站分开登记。当前目录覆盖 39 个经过公开样本确认的大陆黄金品牌/渠道名称；目录类别表示可查询范围，不保证每品牌每日每类都有报价。支持官网为周生生、六福；原周大福等待验证官网保持禁用。国内外行情仍使用上金所日行情、上海金基准价及 Gold API。

2026-10-09 用 `KaungMcpBot` 读取公开页面与 robots 后验证了三个补齐来源：

| 来源 ID | 地址 | 状态 |
|---|---|---|
| `sojinjia` | `https://www.sojinjia.cn/pinpai` | 已验证大陆黄金卡片；逐条日期，香港及非黄金数据排除 |
| `cngolddaily` | `https://cngolddaily.com/brands` | 暂禁用；页面说明价格由基础金价加品牌溢价计算，无逐条日期 |
| `zhenshepin` | `https://www.zhenshepin.com/pinpaijinjia/` | 已验证黄金产品表与逐条日期；排除铂金、换购 |
| `jxgjtz` | `https://jin.jxgjtz.com/pinpaijinjia/` | 已验证品牌表黄金列与逐条内地时间；不读取相邻铂金列 |

每轮先采官网，根据北京时间当日已存官方报价逐品牌、逐品类判断缺失，再按上表顺序补齐。每个网站每轮最多请求一次汇总页，成功结果用于多个品牌。只保存当日仍缺失品类实际采用的数据；不推测页面总更新时间代表逐条报价日期，不用旧第三方报价补今天。

内部 `JewelryQuotes` 保留真实来源、网址、原始产品名称和日期，不改写成官网来源；官网后来更新不删除第三方记录。报价、状态和采集尝试原子写入。历史数据库无需变更表结构，新来源启动时登记。

采集沿用 robots、TLS、SSRF、DNS、重定向和大小限制；不绕过登录、验证码或访问拒绝。失败保留旧报价并指数退避，空数据不保存占位价。同来源锁和品牌轮次锁合并请求；一小时内已尝试的网站不因并发查询再次抓取，手动“立即执行”可强制重试。

## 设置、管理员诊断与验收

```json
{"Jewelry":{"Enabled":true,"BackgroundCollection":true,"MarketIntervalMinutes":15,"BrandIntervalMinutes":60,"TimeoutSeconds":20,"Sources":{}}}
```

`Sources` 可按来源 ID 停用；未验证来源不能通过设置 true 强行启用。默认国际/上海金 15 分钟、品牌 60 分钟；周期配置只初始化默认任务，此后以管理页保存的任务为准。统一品牌目标为 `jewelry.collect_brands`，参数 `{}`。详见 [定时任务](scheduler.md)。

来源状态工具 `kaung_jewelry_get_source_status` 始终对客户端隐藏且禁止调用，即使旧数据库将它启用。管理员在 `/admin/` 的工具测试中仍可查看真实来源状态、错误、最后尝试/成功、最新已存报价日期、下一次尝试与近七天统计。数据新鲜度与进程健康分开判断。

公网部署见 [部署说明](public-mcp-deployment.md)。`McpExposure__JewelryOnly=true` 将公开 MCP 限定为六个金价工具；默认本地工作台仍可使用普通工具。

诊断命令 `dotnet run --project src/KaungMcp -- --jewelry-probe` 使用配置数据库采集并打印内部来源信息后退出。只在本机运行，不把输出作为公开接口；独立测试使用临时数据库和独立设置路径，勿覆盖常用库。

样本验证不等于七天稳定运行。上线后观察每天运行覆盖、错误率和报价日期；七个有记录的 UTC 自然日不代表连续运行 168 小时。具体 COMEX 合约、官网待验证来源和无当日数据的品牌仍明确缺失，不将未完成覆盖宣称为通过。
