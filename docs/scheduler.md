# KaungMcp 定时任务

打开 `http://localhost:5091/admin/`，使用管理员密钥登录，在“定时任务”页创建、编辑、启停和归档任务。修改计划后立即生效。任务可以使用 1–10080 分钟固定周期，或标准五段 Cron 表达式；默认时区为 `Asia/Shanghai`，页面可预览后续五次执行时间。

首次升级会创建三个已启用任务：国际金价 `gold_api` 每 15 分钟，上海金 `sge_daily` 与 `sge_benchmark` 每 15 分钟，品牌金统一补齐任务 `jewelry.collect_brands` 每 60 分钟。旧报价、来源尝试记录继续保留。来源必须在代码目录中通过验证且启用；一个来源不能同时属于两个已启用任务。CME、周大福等未验证来源仍不能通过页面强行启用。

未修改的启用默认品牌任务升级后自动转为统一补齐任务；已编辑、禁用、归档的任务不覆盖。管理页保留迁移提示，可将原任务目标改为品牌金价补齐，参数 `{}`，建议 60 分钟。统一任务先采官网，仅补当日缺失品类；不能与旧品牌来源任务重叠启用。

任务支持内部处理器 `jewelry.collect_brands`（参数 `{}`）与 `jewelry.collect_sources` 和已启用的 MCP 工具。MCP 工具任务保存一份固定参数。对标记为可写或破坏性的工具，保存时需要管理员明确确认；风险标记后来发生变化时，任务执行会阻断，需重新编辑并确认。工具被停用或移除时也会阻断。

“立即执行”不等待下次计划，并可跳过来源失败退避；同一任务运行中再次触发会记为 `skipped`。不同任务最多同时运行四个。服务重启时，未完成的运行记为 `interrupted`，错过的定时点不回放。每次运行状态、摘要与错误永久保存在 `ScheduledTaskRuns`；MCP 工具的完整请求响应保存在现有调用日志，来源标记为 `scheduler`。

管理员 API 位于 `/api/admin` 下：`/scheduler/catalog`、`/scheduler/validate-schedule`、`/scheduled-tasks`、`/scheduled-tasks/{id}/runs` 和 `/scheduled-task-runs/{runId}`。任务详情支持 `enable`、`disable`、`run`、`restore`；`DELETE /scheduled-tasks/{id}` 归档任务并保留运行历史。所有接口使用现有管理员密钥。

当前调度器面向单实例 KaungMcp。查询金价时无缓存或任务逾期可同步补采；已禁用任务不会被查询触发。调度结果供 MCP 查询，不主动注入 Agent 会话。
