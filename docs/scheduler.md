# 插件任务与统一调度

管理页提供网页能力、插件、工具、定时任务及执行记录的关联视图。执行记录使用任务运行和调用日志两个标签，分别保留筛选及分页。插件详情列出处理器、默认模板、已保存计划、最近运行和配置；定时任务可按插件筛选，编辑参数、间隔/Cron、时区、超时及重试，预览未来五次执行，启停或立即执行。运行详情展示参数快照、插件与定义版本、触发来源、结果、错误、关联 ID 和调用日志。

## 数据模型与规则

- 任务定义由插件注册：处理器 ID、参数 Schema、定义版本、默认及最大超时、幂等声明和业务校验。
- 任务计划保存单个处理器或显式允许调度的 MCP 工具及固定参数。目标不可变更；变更目标时创建新计划。
- 运行记录保存执行快照，独立于计划启停/删除；删除计划不删除历史。
- 插件默认模板首次创建一次，管理员编辑后不覆盖，删除后不重新生成。默认模板本身不作为执行队列。
- 参数不兼容、插件缺失/停用、工具禁用、目标声明变化等作为计划阻塞原因展示，不每秒生成失败记录。
- 可写/破坏性工具需要管理员保存无人值守确认，风险声明改变必须重新确认。任务调度不能绕过工具权限。

## 时间与执行

固定周期支持 1–10080 分钟，以原计划时间推进；编辑或重新启用后从当前时间重新计算。Cron 为标准五段，默认时区 `Asia/Shanghai`，使用 Cronos 处理时区与夏令时；数据库时间统一保存 UTC ticks，页面按浏览器本地时间显示执行时间。

宿主每秒扫描计划，在同一 SQLite 事务中创建待执行记录并推进下次时间，以 `(TaskId, PlannedTicks)` 唯一索引防重复入队。执行器领取持久化任务，为每次执行创建独立 DI 作用域。默认全局 4 个、每插件 2 个并发，可通过 `Scheduler:MaxConcurrency` / `Scheduler:PluginConcurrency` 调整。同一计划重复触发记为 `skipped`；资源并发键冲突的不同计划排队等待。

默认超时 300 秒，允许在目标给出的上限内调整。默认不重试，声明幂等的目标最多重试 2 次，等待 30 秒、120 秒；重试继续校验插件、权限、定义版本、风险及并发约束。尝试共用运行 ID 和关联 ID，调用日志记录每次执行。

“立即执行”返回 HTTP 202 与运行 ID，通过同一执行器处理。可以执行停用的计划，仍受插件启停、权限和并发约束。排队任务取消后直接变为 `cancelled`；运行中取消只保存“已请求取消”并传递令牌，处理器实际结束后才确定状态。超时同样使用协作取消，可信插件不响应令牌时会继续占用执行槽直到实际结束。

重启后原 `running` 标记为 `interrupted`，不自动重放；排队任务重新校验后执行。停机期间错过的执行点跳过并保存遗漏摘要，不逐次补跑。业务应自行确保幂等，不承诺业务效果恰好一次。

运行状态：`queued`、`running`、`succeeded`、`failed`、`cancelled`、`timed_out`、`interrupted`、`skipped`。

## 首批计划

| 插件 | 稳定模板 ID | 目标 | 参数 | 周期 |
|---|---|---|---|---|
| 珠宝 | international | collect_market | sourceIds: gold_api | 15 分钟，启用 |
| 珠宝 | shanghai | collect_market | sourceIds: sge_daily、sge_benchmark | 15 分钟，启用 |
| 珠宝 | brands | collect_brands | {} | 60 分钟，启用 |

诊断插件、内置网页模块无默认任务。网页工具显式允许调度，可以由管理员创建计划。珠宝插件负责校验来源验证/启用状态、最低间隔和来源冲突；宿主没有珠宝专用调度分支。珠宝查询只读缓存，首次数据通过计划或管理员立即执行采集。

## 管理 API

全部使用 `X-Kaung-Admin-Key`，前缀 `/api/admin`。

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | /plugins | 状态、版本、健康、错误 |
| PUT | /plugins/{id}/enabled | {isEnabled: true/false} |
| GET | /plugins/{id}/task-definitions | 处理器、模板、配置定义 |
| GET/POST | /scheduled-tasks | 分页查询/创建 |
| GET/PUT/DELETE | /scheduled-tasks/{id} | 查看/编辑/删除，保留历史 |
| POST | /scheduled-tasks/preview?planId={id} | 校验参数及未来五次时间，编辑时提供 planId |
| POST | /scheduled-tasks/{id}/enable 或 disable | 启停计划 |
| POST | /scheduled-tasks/{id}/run | 返回 {runId} |
| GET | /scheduled-tasks/{id}/runs | 当前计划的运行历史 |
| GET | /task-runs | 按 pluginId 分页查询 |
| GET | /task-runs/{id} | 状态、快照、结果和日志链接 |
| POST | /task-runs/{id}/cancel | 取消排队任务或请求取消 |

创建示例（诊断插件已启用）：

```json
{
  "name": "每小时连接检查",
  "pluginId": "diagnostics",
  "targetType": "tool",
  "targetId": "kaung_ping",
  "arguments": {},
  "scheduleType": "cron",
  "cronExpression": "0 * * * *",
  "timeZoneId": "Asia/Shanghai",
  "isEnabled": true,
  "timeoutSeconds": 300,
  "maxRetries": 0,
  "confirmRisk": false
}
```

首版只支持单实例、单目标；不包含分布式领取、插件市场、业务工作流或向 Agent 会话主动推送结果。

## 归属与执行记录查询

计划创建和编辑支持 `ownerId`，旧 `pluginId` 字段仍有效；同时提供时必须一致。网页模块的归属 ID 仍为 `web`，不改变已保存计划和历史。返回值提供 `ownerId`、`ownerKind`（module/plugin）、`ownerName` 和 `targetName`；运行记录另提供 `taskName`。

`GET /api/admin/task-runs` 支持 `ownerId`/`pluginId`、`ownerKind`、`taskId`、`status`、`trigger`、`fromUtc`、`toUtc`、`correlationId`，筛选后分页。`GET /api/admin/call-logs` 新增 `ownerId`、`ownerKind`、`correlationId`，保留既有筛选，详情提供关联 `taskRunId`。

日志归属字段可空，升级根据关联运行及已知能力补全；无法确认的历史请求显示“归属未知”。停用工具立即阻塞其计划并取消排队任务，正在执行的任务允许完成。启用后重新计算后续执行时间。
