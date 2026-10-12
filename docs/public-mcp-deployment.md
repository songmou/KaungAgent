# 公网纯数据 MCP 部署

部署模板位于 `infra/public-mcp/`：systemd 服务、环境变量示例、Nginx HTTPS 配置。模板面向 Linux 单实例，不会自动部署、申请证书或修改本机网络。

## 1. 准备服务

安装兼容 .NET 10 的 ASP.NET Core Runtime、Nginx 和 SQLite 命令行工具。创建专用系统用户 `kaungmcp`；创建 `/opt/kaungmcp/app` 、`/opt/kaungmcp/plugins` 与 `/etc/kaungmcp`。在开发机执行：

```sh
dotnet publish src/KaungMcp -c Release -o /tmp/kaungmcp-publish
```

将发布目录内容复制到服务器 `/opt/kaungmcp/app`，只将其中的 `plugins/jewelry` 复制到独立安装目录 `/opt/kaungmcp/plugins/jewelry`。不要安装诊断插件；网页模块由宿主内置，通过 WebTools__SearchEnabled=false 和 WebTools__FetchEnabled=false 关闭两个网页工具。复制 `service.env.example` 为 `/etc/kaungmcp/service.env`，替换随机管理员密钥、域名；文件由 root 持有，权限 `600`。复制服务模板到 `/etc/systemd/system/kaungmcp.service`。systemd 自动创建仅服务用户可写的 `/var/lib/kaungmcp`。

这是全新系统部署；宿主创建 host.db，珠宝插件创建自己的 plugin.db。首次部署需创建客户端。

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now kaungmcp
curl --fail http://127.0.0.1:5091/health/ready
sudo journalctl -u kaungmcp -n 50
```

仅金价采集不需要 SearXNG 和浏览器。模板关闭两个网页工具及搜索/浏览器依赖，安装目录仅放 jewelry 插件，使客户端只能发现和调用六个公开金价工具。来源状态工具始终禁止客户端发现和调用。管理员仍可内部测试注册的工具。

应用监听由 `McpHost__LocalPort` 控制；设置 `ASPNETCORE_URLS` 不会覆盖代码中的回环监听。`AllowedHosts` 必须包含公网域名。管理页保存的设置优先于环境变量，配置保存在宿主数据库中，请检查域名、数据库路径和监听覆盖项；需要重启的设置应通过 systemd 重启生效。

## 2. HTTPS 与管理入口

准备域名 DNS 和可信 HTTPS 证书，替换 `nginx.conf.example` 的域名与证书路径，安装到 Nginx 的 `http` 配置上下文。使用发行版的证书续期机制管理证书。

```sh
sudo nginx -t
sudo systemctl reload nginx
```

防火墙只开放所需 SSH 和 443；HTTP 80 仅在证书验证需要时开放。Nginx 只代理精确 `/mcp`，其余路径返回 404。5091 只监听回环；无需开启局域网 HTTPS。反向代理关闭缓冲和缓存，超时 180 秒，支持 MCP 流式返回和多个网站顺序补采。应用只信任本机代理传来的地址和 HTTPS 协议头。

通过 SSH 隧道打开管理页：

```sh
ssh -L 15091:127.0.0.1:5091 user@server
```

本机访问 `http://127.0.0.1:15091/admin/`，用管理员密钥登录。为每个外部调用方创建独立客户端，妥善保存只展示一次的 API Key。外部地址为 `https://实际域名/mcp`，请求头使用 `Authorization: Bearer 客户端密钥`，不能使用管理员密钥。

## 3. 采集设置与首次运行

- 启用 `Jewelry__Enabled=true`。国际金价、上海金任务周期设为 15 分钟。
- 首次加载珠宝插件创建 international、shanghai、brands 三个默认启用计划。品牌处理器为 `collect_brands`，参数 `{}`，周期 60 分钟，时区 `Asia/Shanghai`。
- 周期和启停以管理页保存的计划为准；升级不覆盖用户编辑，也不恢复已删除的默认计划。
- 启用服务后在管理页对三个任务“立即执行”，验证运行摘要。重启不会逐次回放停机期间错过的任务。
- 在管理页测试内部 `kaung_jewelry_get_source_status` 查看最后成功时间、下一次尝试、最新已保存报价日期和错误。采集成功不等于网站发布了当天的新价。

优先使用官网。当日某品类缺失时依次尝试搜金价、今日金价网、珍奢品、jxgjtz；只有通过验证且启用的来源参与。“今日金价网”样本明确采用基础金价加溢价的计算报价且无逐条日期，因此暂时禁用。补齐只保存实际采用的当日报价。查询 `today` 没有当天数据时返回缺失，旧价在 `latestAvailable` 单列。

## 4. 验收、备份与恢复

用支持 Streamable HTTP 的 MCP 客户端连接公网端点，检查：

1. 无密钥和错误密钥被拒绝；有效客户端能初始化、发现六个金价工具并调用报价。
2. 目录、报价、历史、比较和换算输出不含网站名称、来源标识、网址或原始抓取错误；品牌查询无 `sourceId` 参数。
3. 来源状态工具不被发现，直接调用被拒绝；管理员仍可内部测试该工具。
4. 公网 `/admin/`、`/api/admin/clients`、`/health/ready`、`/` 和 `/mcp/` 返回 404；只开放精确 `/mcp`。
5. 用需要流式响应的 MCP 请求确认代理持续传递数据，不等待缓冲；重启服务后定时任务继续运行。

上线后观察七天：每个来源的每天运行覆盖、失败与退避、报价日期是否符合更新规律。七个有记录的自然日不代表持续运行 168 小时或通过稳定性验收。

运行中的数据库用 SQLite 在线备份：

```sh
sudo -u kaungmcp sqlite3 /var/lib/kaungmcp/host.db ".backup '/var/lib/kaungmcp/backup.db'"
```

宿主库与 `/var/lib/kaungmcp/plugins/jewelry/plugin.db` 分别在线备份，并备份产物目录。需要跨库一致的快照时停止服务后一起备份。将备份复制到受保护的备份目录，限制权限并制定保留周期。恢复时停止服务，保存当前数据库及 WAL/SHM，使用备份替换数据库、移走旧 WAL/SHM，修正服务用户所有权后启动，核对健康检查、客户端及任务。上线失败时停止新版本并恢复旧程序及配套数据库/配置；插件升级同样需要停机，并保留对应版本的程序与数据库备份。

网页工具的数据库开关优先于 WebTools 环境默认值。复用已有数据库部署专用珠宝服务时，先在工具管理中停用网页搜索和读取，并检查 MCP 工具目录仅公开六个金价工具。
