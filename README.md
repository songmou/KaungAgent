# KaungAgent

KaungAgent 是一个基于 C#、.NET 10、Blazor 和 SQLite 的本地 Agent/MCP 工作台。它提供可持久化的大模型对话、OpenAI 兼容模型管理、多 MCP 接入、能力发现与工具调用闭环。

## 项目结构

```text
KaungAgent.sln
├── src
│   ├── KaungAgent       # Agent 平台入口和 MCP 客户端代理
│   └── KaungMcp         # MCP HTTP 服务、鉴权、客户端管理和调用日志
└── tests
    ├── KaungAgent.Tests
    └── KaungMcp.Tests
```

两个服务独立运行：

- KaungMcp 默认监听 `http://localhost:5091`，MCP 端点为 `/mcp`。
- KaungAgent 默认监听 `http://localhost:5090`，通过 MCP Streamable HTTP 调用 KaungMcp。
- KaungMcp 使用 SQLite 保存客户端身份、调用日志及请求响应详情。

## 已实现能力

### KaungMcp

- 官方 MCP C# SDK 2.2.0，无状态 Streamable HTTP。
- Bearer API Key 鉴权，数据库只保存 SHA-256 哈希。
- 管理 API：创建、查询、启用、禁用、轮换和软删除客户端；按分类查看、启停和测试 MCP 工具。
- 按客户端、工具、结果和时间范围查询调用日志。
- 调用日志预留 Token、计费单位和微单位成本字段。
- 内置 `kaung_ping`、`kaung_echo`、`kaung_web_search`、`kaung_web_fetch` 工具，`kaung_connection_diagnostic` 提示词，以及 `kaung://server/status` 只读状态资源。
- 珠宝行情模块提供七个 `kaung_jewelry_*` 工具：品种目录、国内外行情、品牌报价、历史、比较、单位/汇率换算和来源状态。当前启用上金所日行情及基准价、Gold API、周生生和六福；COMEX 和未验证品牌明确报告不可用，不用旧价或其他品种替代。配置、示例及尚未完成的验收项见 [珠宝行情 MCP](docs/jewelry-mcp.md)。
- 数据库驱动的定时任务中心：在 KaungMcp 管理页创建固定周期或 Cron 任务，管理金价采集和已启用的 MCP 工具调用，查看每次运行及关联日志。使用说明见 [定时任务](docs/scheduler.md)。
- 原生接入 SearXNG 网页搜索，并在 SearXNG 未配置、禁用或暂时不可用时直接抓取 Bing HTML 搜索结果作为尽力而为的托底；网页正文读取包含 robots.txt、SSRF、重定向和响应大小保护，并在静态 HTML 不可读时使用隔离 Chromium 渲染。
- EF Core Migration、SQLite WAL 和数据库启动迁移。

### KaungAgent

- Blazor 统一工作台：首页、对话、模型管理和 MCP 集成。
- OpenAI Chat Completions 兼容模型，支持流式输出和自动工具调用。
- 模型返回上下文超限错误时自动压缩旧对话、保存可见摘要并重试，原始消息不会删除。
- 模型配置、会话、消息、Token、工具轨迹和能力快照持久化。
- 多 HTTP MCP 注册，自动兼容 Streamable HTTP 与旧 SSE。
- MCP tools、prompts、resources 和 resource templates 发现与交互。
- 模型和 MCP 凭据使用 Data Protection 加密保存。
- 可注入的 `IMcpGatewayClient` 和原有 MCP 工具代理兼容接口。
- 独立 API Key 保护 Agent HTTP API。
- 上游连接失败、超时、鉴权失败和未知工具的 Problem Details 映射。
- 存活与就绪检查。

## 环境要求

- .NET SDK 10.0.301 或兼容的更新版本。
- Docker 与 Docker Compose 为可选依赖，用于启动推荐的本地 SearXNG；没有 Docker 或显式禁用 SearXNG 时仍可使用静态 HTML 搜索托底。
- macOS、Linux 或 Windows。

还原、编译和测试：

```bash
dotnet tool restore
dotnet restore KaungAgent.sln
dotnet build KaungAgent.sln
dotnet test KaungAgent.sln
```

## 本地启动

### 一键启动（推荐）

在 macOS 或 Linux 中，从项目根目录运行：

```bash
./start.sh
```

首次启动不需要输入任何内容。脚本会自动生成 KaungMcp 管理员密钥和 KaungAgent API 密钥，随后自动还原和编译项目、启动 KaungMcp、创建本地 MCP 客户端，再启动 KaungAgent 并验证完整调用链。

启动脚本还会启动默认仅绑定回环地址的本地 SearXNG，并安装与项目版本匹配的 Playwright Chromium headless shell。若默认端口 8080 已占用，脚本会自动在 8081–8099 中选择可用端口，不会停止或覆盖原有服务；也可以显式设置 `SEARXNG_PORT`。若已有独立 SearXNG，可在启动前设置 `SEARXNG_ENDPOINT`，此时脚本只检查该端点，不管理其生命周期。

不希望运行 SearXNG 时，可以直接启动静态搜索托底：

```bash
SEARXNG_DISABLED=1 ./start.sh
```

搜索链默认优先使用 SearXNG；连接失败、超时或服务端异常时自动改为直接抓取 Bing HTML 搜索页，清洗标题和摘要、去重，并将 Bing 中转链接还原为目标网页 URL。返回结果的 `provider` 为 `bing-html`，`warnings` 会明确标记托底已启用。该方式依赖公开页面结构，可能受到页面改版、验证码或限流影响，因此适合作为应急托底，不替代稳定的 SearXNG 部署。

本地密钥和 MCP 客户端信息保存在被版本控制忽略的 `.kaung/local.env` 中，文件权限为 `600`。后续运行 `./start.sh` 会直接复用该配置。需要自动生成全新的密钥，或者本地数据库已重建时，运行：

```bash
./start.sh --reconfigure
```

两个服务的日志会显示在同一个终端中。按 `Ctrl+C` 可同时停止 KaungAgent 和 KaungMcp。运行 `./start.sh --help` 可以查看命令帮助。

服务就绪后会自动打开：

- Agent 工作台：`http://127.0.0.1:5090/`
- KaungMcp 管理页：`http://127.0.0.1:5091/admin/`（首页提供入口）

KaungMcp 管理页使用 `.kaung/local.env` 中的 `KAUNG_MCP_ADMIN_KEY` 登录；验证后密钥仅保存在当前浏览器标签页的会话存储中。页面可以按分类查看工具名称、描述、参数 Schema 和行为提示，逐工具启停并使用表单或原始 JSON 测试调用；停用会立即阻止客户端发现和调用，但不影响管理员测试。页面也可以管理定时任务、MCP 客户端、调用日志，以及搜索、网页读取和服务设置。客户端密钥仅在创建或轮换时展示一次。对 `kaung-agent-local` 客户端轮换、停用或删除会中断 Agent 连接；轮换后需同步 `.kaung/local.env` 中的 `KAUNG_MCP_API_KEY` 并重启 Agent。下一次一键启动会检测失效的本地客户端密钥并重新配置客户端。

页面保存的配置存放在权限为 `600` 的 `.kaung/mcp-settings.json`，按字段覆盖环境变量和 `appsettings.json`；使用“恢复”可清除单字段覆盖。网页搜索（包括 SearXNG 搜索开关及地址）和网页读取设置对后续请求立即生效。监听、数据库、日志级别和 SearXNG 容器启动方式需重启一键启动脚本后生效。切换数据库只接受已有、结构兼容的 SQLite 文件，不会复制原库的客户端和日志。

局域网访问默认关闭。启用后，在管理页填写本机的私有 IPv4 地址和 HTTPS 端口，应用会生成自签证书；重启后仅在该地址开放 HTTPS，本机 Agent 仍使用回环 HTTP。其他设备需要先信任管理页提供的公有证书，并核对页面显示的 SHA-256 指纹。不要将局域网监听地址设为公网地址。

在服务器、CI 或不希望打开浏览器时使用：

```bash
./start.sh --no-open
```

`--reconfigure` 和 `--no-open` 可以组合使用。

如需自动配置 DeepSeek，在 `.kaung/local.env` 中设置 `DEEPSEEK_API_KEY`。启动时 KaungAgent 会幂等创建或更新启用的 `DeepSeek Flash` 模型（`deepseek-flash`，Base URL 为 `https://api.deepseek.com`），并仅在当前没有其他可用默认模型时将它设为默认。模型名称、端点、模型 ID、启用状态和默认状态都属于“模型管理”功能，并持久化在 SQLite 的 `ModelProfiles` 表中；环境文件只负责向启动过程提供不应提交到版本库的明文密钥，数据库中的密钥使用 Data Protection 加密。`--reconfigure` 会保留该密钥。

如果端口已经被占用，启动脚本会在错误信息中显示占用进程的 PID 和进程名。通常这表示服务已经启动，无需重复运行脚本。

## 工作台使用

1. 打开“模型管理”，添加一个 OpenAI Chat Completions 兼容服务的 Base URL、模型 ID 和凭据。首次保存的可用模型会自动成为默认模型。
2. 打开“MCP 集成”。本地 KaungMcp 会由启动脚本自动注册；也可以添加任意 HTTP MCP，并以 JSON 配置静态请求头。
3. 点击“连接并发现”读取服务信息、工具、提示词和资源。工具既可通过 JSON Schema 表单调用，也可切换到原始 JSON。
4. 打开“对话”创建会话，按服务或工具选择允许模型使用的 MCP。模型请求工具时会自动执行，最多连续 8 轮，并将参数、结果、耗时和错误保存到会话记录。

当模型明确返回上下文长度超限错误时，KaungAgent 会使用同一模型压缩较早的完整对话轮次，将摘要显示并保存在会话中，然后自动重试当前请求。普通参数错误不会触发压缩；单次模型调用最多尝试两轮压缩恢复。

模型 API Key 和自定义请求头不会返回到浏览器；页面只显示是否已配置以及请求头名称。更换凭据时输入新值，编辑时留空会保留原值。

### 手动启动

#### 1. 启动 KaungMcp

管理员密钥至少需要 32 个字符，不要把真实密钥写入配置文件或提交到版本库。

```bash
export KAUNG_MCP_ADMIN_KEY="replace-with-a-random-admin-key-at-least-32-characters"
dotnet run --project src/KaungMcp
```

服务首次启动时会自动创建 `data/kaungmcp.db` 并执行数据库迁移。

#### 2. 创建 MCP 客户端

```bash
curl --request POST http://localhost:5091/api/admin/clients \
  --header "X-Kaung-Admin-Key: $KAUNG_MCP_ADMIN_KEY" \
  --header "Content-Type: application/json" \
  --data '{"name":"kaung-agent","description":"Local KaungAgent service"}'
```

响应中的 `apiKey` 只会在创建或轮换时返回一次。将其安全地保存到另一个终端：

```bash
export KAUNG_MCP_API_KEY="kmcp_key-id.secret-from-create-response"
export KAUNG_AGENT_API_KEY="replace-with-a-random-agent-key-at-least-32-characters"
export KAUNG_MCP_ENDPOINT="http://localhost:5091/mcp"
export DEEPSEEK_API_KEY="replace-with-your-deepseek-api-key"
dotnet run --project src/KaungAgent
```

#### 3. 验证完整调用链

发现 MCP 工具：

```bash
curl http://localhost:5090/api/mcp/tools \
  --header "X-Kaung-Agent-Key: $KAUNG_AGENT_API_KEY"
```

调用 Echo：

```bash
curl --request POST http://localhost:5090/api/mcp/tools/kaung_echo/invoke \
  --header "X-Kaung-Agent-Key: $KAUNG_AGENT_API_KEY" \
  --header "Content-Type: application/json" \
  --data '{"arguments":{"message":"Hello from KaungAgent"}}'
```

查询调用日志：

```bash
curl http://localhost:5091/api/admin/call-logs \
  --header "X-Kaung-Admin-Key: $KAUNG_MCP_ADMIN_KEY"
```

## HTTP API

### KaungAgent

| 方法 | 路径 | 鉴权 | 说明 |
| --- | --- | --- | --- |
| GET | `/health/live` | 无 | 进程存活检查 |
| GET | `/health/ready` | 无 | MCP 连接与工具发现检查 |
| GET | `/api/mcp/tools` | `X-Kaung-Agent-Key` | 获取工具列表 |
| POST | `/api/mcp/tools/{name}/invoke` | `X-Kaung-Agent-Key` | 调用指定工具 |

### KaungMcp

| 方法 | 路径 | 鉴权 | 说明 |
| --- | --- | --- | --- |
| POST | `/mcp` | `Authorization: Bearer ...` | MCP Streamable HTTP |
| GET | `/health/live` | 无 | 进程存活检查 |
| GET | `/health/ready` | 无 | SQLite 就绪检查 |
| GET | `/health/web` | 无 | SearXNG、静态搜索托底、HTTP 抓取器和 Chromium 状态 |
| GET | `/api/admin/tools` | `X-Kaung-Admin-Key` | 按分类读取工具定义与启用状态 |
| PUT | `/api/admin/tools/{name}/enabled` | `X-Kaung-Admin-Key` | 启用或停用工具 |
| POST | `/api/admin/tools/{name}/test` | `X-Kaung-Admin-Key` | 使用管理身份测试工具并记录日志 |
| POST | `/api/admin/clients` | `X-Kaung-Admin-Key` | 创建客户端并返回一次性明文密钥 |
| GET | `/api/admin/clients` | `X-Kaung-Admin-Key` | 分页查询客户端 |
| POST | `/api/admin/clients/{id}/enable` | `X-Kaung-Admin-Key` | 启用客户端 |
| POST | `/api/admin/clients/{id}/disable` | `X-Kaung-Admin-Key` | 禁用客户端 |
| POST | `/api/admin/clients/{id}/rotate-key` | `X-Kaung-Admin-Key` | 轮换客户端密钥 |
| DELETE | `/api/admin/clients/{id}` | `X-Kaung-Admin-Key` | 软删除客户端 |
| GET | `/api/admin/call-logs` | `X-Kaung-Admin-Key` | 分页查询调用日志 |
| GET | `/api/admin/call-logs/{id}` | `X-Kaung-Admin-Key` | 查询单条日志及请求参数、接口输出 |
| GET/PUT | `/api/admin/settings` | `X-Kaung-Admin-Key` | 读取、保存及恢复管理配置 |
| POST | `/api/admin/settings/validate` | `X-Kaung-Admin-Key` | 校验拟保存配置并预览待重启项，不写入文件 |
| POST | `/api/admin/admin-key/rotate` | `X-Kaung-Admin-Key` | 轮换管理员密钥并同步本地启动配置 |
| GET | `/api/admin/certificate` | `X-Kaung-Admin-Key` | 下载局域网 HTTPS 公有证书 |

分页参数 `pageSize` 的最大值为 100。调用日志支持 `clientId`、`source`（`mcp_client`、`admin_test` 或 `scheduler`）、`method`、`toolName`、`succeeded`、`fromUtc` 和 `toUtc` 筛选；已鉴权的 MCP 请求（初始化、能力查询、工具、提示词、资源等）按客户端记录方法、耗时、结果、错误摘要和 TraceId，后台工具测试与定时任务调用以独立来源记录。请求参数和接口输出会保存，不采集 HTTP 鉴权头。正文仅通过管理员详情接口按需读取。管理页每条记录的“详情”弹窗支持格式化查看与分别复制，关闭后保留筛选和页码。升级前的历史记录仍保留，未保存的参数和输出会明确提示；新请求没有参数时显示 `null`，未捕获到响应时显示相应提示。通知和鉴权失败的请求不归入客户端日志。

## 配置

| 环境变量 | 服务 | 用途 |
| --- | --- | --- |
| `KAUNG_MCP_ADMIN_KEY` | KaungMcp | 管理 API 密钥，至少 32 个字符 |
| `ConnectionStrings__KaungMcp` | KaungMcp | SQLite 连接字符串 |
| `KAUNG_AGENT_API_KEY` | KaungAgent | Agent API 密钥，至少 32 个字符 |
| `KAUNG_MCP_API_KEY` | KaungAgent | 调用 KaungMcp 的客户端密钥 |
| `KAUNG_MCP_ENDPOINT` | KaungAgent | MCP HTTP 端点 |
| `DEEPSEEK_API_KEY` | KaungAgent | 可选；自动创建或更新 `deepseek-flash` 模型配置 |
| `SEARXNG_ENDPOINT` | KaungMcp/启动脚本 | 可选；外部 SearXNG 根地址，未设置时尝试启动本地实例 |
| `SEARXNG_PORT` | 启动脚本/Compose | 可选；显式指定本地 SearXNG 回环端口；未设置时从 `8080` 开始自动选择 |
| `SEARXNG_DISABLED` | KaungMcp/启动脚本 | 可选；设为 `1` 或 `true` 时跳过 SearXNG，直接启用静态搜索托底 |
| `STATIC_SEARCH_ENDPOINT` | KaungMcp | 可选；静态搜索页地址，默认 `https://www.bing.com/search` |
| `WebSearch__StaticFallback__Enabled` | KaungMcp | 可选；设为 `false` 时关闭静态搜索托底 |

KaungAgent 工作区数据库默认位于 `data/kaungagent.db`，Data Protection 密钥默认位于 `.kaung/keys`。KaungMcp 数据库仍位于 `data/kaungmcp.db`。

## 当前边界

- 仅面向 localhost 单用户使用，不包含登录和公网部署配置。
- 模型协议为 OpenAI Chat Completions 兼容接口，暂不包含厂商专有协议和多模态附件。
- MCP 客户端仅支持 HTTP；鉴权使用无鉴权或静态自定义请求头，暂不包含 OAuth 和 stdio。
- 网页抓取只读取公开 HTTP/HTTPS 页面，不访问私网、登录页面、付费墙、PDF、媒体文件，也不执行递归站点爬取。
- KaungMcp 的 5091 首页保留服务状态入口，管理功能位于 `/admin/`。
