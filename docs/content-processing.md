# 内容处理 MCP

内容插件提供中英文 OCR、文档解析和音频文件转写。客户端先上传文件或导入公开下载链接，再提交异步任务，查询结果并分段读取或下载产物。无需让大模型接收二进制附件。

## 安装与部署

`content` 与诊断、珠宝插件一样默认随宿主构建和发布。运行 `./start.sh` 时，全新安装会安装全部内置插件；已有 `.kaung/plugins` 会自动补装新增的内容插件，重启后即可在“插件管理”“工具管理”和“内容处理”看到它，无需手动发布。脚本记录已分发插件，后续不会覆盖已有包或恢复主动卸载的插件。

独立部署时使用宿主发布目录内的 `plugins/content`。曾主动卸载内容插件的安装，可以停止宿主后重新安装：

```sh
dotnet publish src/KaungMcp.Plugins.Content -c Release -o .kaung/plugins/content
```

引擎验收基线为 Linux x64、4 核、16 GB 内存、无 GPU。模型计算与 LibreOffice 在独立引擎子进程中执行，宿主负责持久化队列；内容计算默认串行，不阻止其他插件工作。

本地 Docker 部署可以一次性运行准备脚本，再重启宿主：

```sh
bash scripts/start-content-engine.sh
./start.sh --no-open
```

脚本复用现有引擎镜像，缺少时自动构建；缺少模型时显式下载到持久卷，随后等待三项能力就绪。内部凭据保存在忽略提交的 `.kaung/content-engine.env`（权限 600），正常启动脚本自动读取；不随管理员密钥轮换。容器由 Docker 持续管理，不随 Agent 终端退出而停止。已准备的模型可通过 `CONTENT_ENGINE_MODEL_SOURCE=/绝对路径` 离线复制到持久卷。部署时须保证 Docker 可用；启动后刷新插件状态，健康结果最多缓存 30 秒。

独立部署也可以手动生成一个随机的内部凭据，供宿主和引擎共用；不要提交凭据：

```sh
export CONTENT_ENGINE_KEY="$(openssl rand -hex 32)"
docker compose -f infra/content-engine/compose.yml build
docker compose -f infra/content-engine/compose.yml run --rm engine python prefetch.py
docker compose -f infra/content-engine/compose.yml up -d
./start.sh --no-open
```

凭据需要通过服务环境持久化。宿主支持 `CONTENT_ENGINE_KEY` 或优先级更高的 `Content__EngineKey`，引擎使用 `ENGINE_KEY`。凭据不会出现在管理配置接口中。不要每次启动重新生成凭据。

`prefetch.py` 显式联网准备 OCR、Docling、Whisper small 模型，并在模型卷写入 `model-lock.json` 校验清单。备份并复用整个模型卷，升级前保留旧清单；处理请求使用本地模型，不自动下载模型。缺少模型时，对应能力返回未就绪，文本和 Excel 仍可独立处理。

Python 依赖完整固定在 `infra/content-engine/requirements.lock`，模型仓库提交固定在 `model-sources.json`。重新解析依赖使用：

```sh
uv pip compile infra/content-engine/requirements.in --universal --python-version 3.11 --torch-backend cpu -o infra/content-engine/requirements.lock
```

Compose 只在 `127.0.0.1:8092` 发布引擎，使用非 root 用户、只读容器、内部凭据和独立临时目录。宿主与引擎分机部署时使用受保护的内部网络，配置可信的 `Content:EngineEndpoint`。该地址由管理员设置，不经过公开文件 URL 下载策略。

## 管理页

打开 `/admin/` 的“内容处理”：

1. 上传文件或导入公开下载链接。
2. 选择文档解析、OCR 或音频转写，按需选择页范围、工作表、编码和时间戳。
3. 提交后查看状态，可取消排队或运行任务。
4. 预览结果、继续读取后续文本，或下载完整文件。

页面通过现有工具测试接口提交请求，调用记录与异步执行记录相关联。完整历史保留在“执行记录”。参数和依赖限制在“插件管理 → 内容处理 → 配置”维护。

## 文件接口

客户端接口使用与 `/mcp` 相同的 Bearer Key：

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/api/files` | multipart/form-data，单个 `file` 文件 |
| GET | `/api/files/{fileId}` | 名称、类型、大小、有效期 |
| GET | `/api/files/{fileId}/download` | 下载原文件或结果 |
| GET | `/api/files/{fileId}/text?startIndex=0&maxCharacters=8000` | 分段读取生成的文本结果 |
| DELETE | `/api/files/{fileId}` | 删除文件 |

管理员对应接口前缀为 `/api/admin/files`，使用 `X-Kaung-Admin-Key`。客户端只能访问自己的文件和任务，管理员可查看全部。不存在、越权文件均返回 404；被排队或运行任务引用的文件返回 409；超限返回 413。文件名不构成存储路径，服务不接受服务器目录或路径。

原文件默认存储在宿主数据库旁的 `files/`，可通过 `Content__FilesDirectory` 指定启动路径。产物位于插件数据目录 `content/artifacts/`；文件 ID 到路径的映射由宿主保存，不向客户端返回路径。

## MCP 工具与结果

| 工具 | 参数 |
|---|---|
| `kaung_file_import` | `url` |
| `kaung_file_info` / `kaung_file_delete` | `fileId` |
| `kaung_ocr_recognize` | `fileId`、可选 `pages` |
| `kaung_document_parse` | `fileId`、`pages`、`ocrMode=auto/force/off`、`sheetNames`、`encoding=auto/utf-8/utf-16/gb18030` |
| `kaung_audio_transcribe` | `fileId`、`language=auto/zh/en`、`wordTimestamps=false` |
| `kaung_content_job_get` / `kaung_content_job_cancel` | `jobId` |
| `kaung_content_read` | 结果 `fileId`、`startIndex=0`、`maxCharacters=8000` |

识别和解析提交后立即返回 `jobId`。首期识别与解析处理器仅接受一次性任务，周期调度仅用于过期清理。查询结果的 `result.files` 包含输出文件，`result.warnings` 表示低置信度、部分失败或没有识别到内容。支持的任务状态：`queued`、`running`、`succeeded`、`failed`、`cancelled`、`timed_out`、`interrupted`、`skipped`。

结果包含可读文本及 JSON；表格额外导出 CSV，音频额外导出 SRT/VTT。JSON 包含 `schemaVersion=1`、`operation`、`text` 和相应的 `blocks/tables/sheets/segments`。OCR 坐标基于返回的渲染图片尺寸；PDF 正文和表格保留实际页码及坐标来源。Word 不生成推测页码。

`kaung_content_read` 只读取生成的 UTF-8 文本产物；每次最多 20,000 字符。偏移以 UTF-16 字符计数，使用返回的 `nextStartIndex` 继续读取。JSON 分段以字符串返回；需要完整 JSON 时下载文件后解析。调用日志记录文件 ID、参数及有界响应，不保存上传二进制或完整结果文件。

Excel 保留公式和已有缓存值，不执行计算、宏或外部链接。旧版 XLS 额外保留 BIFF 公式 token；不能还原的公式表达式为 null，可从 token 追溯。CSV 导出对可能执行公式的文本加前导单引号，原始单元格信息保留在 JSON。

## 限额、生命周期与兼容性

| 配置 | 默认值 |
|---|---|
| `Content:MaxFileBytes` | 104857600（100 MiB，原文件和单任务总产物） |
| `Content:MaxPdfPages` | 200 |
| `Content:MaxAudioMinutes` | 120 |
| `Content:JobTimeoutSeconds` | 7200（仅计算执行时间） |
| `Content:RetentionDays` | 7 |

防止解压和解码异常还设有固定上限：Office 展开内容 512 MiB、10,000 个 ZIP 条目，表格 1,000,000 单元格，图片或 PDF 单页渲染 40,000,000 像素，单任务最多 255 个结果文件。含宏、加密或损坏文件会明确失败。

每日清理由宿主调度器执行，跳过排队和运行任务引用的输入文件。保留时间从每个文件创建时计算，配置变化作用于新文件。取消会向引擎显式发送终止请求，并终止处理子进程及其后代。重启将运行任务标记为 interrupted，不自动重放；排队任务继续保留。插件停用取消排队任务，允许已运行任务完成。

通过增量 EF Migration 增加文件元数据、任务提交者身份，并允许任务没有计划 ID；已有客户端、配置、计划和运行记录保持兼容。插件契约版本仍为 1，新增 `IPluginJobService`，任务须显式设置 `AllowOneOff=true` 才能接受一次性执行。

首期不包括聊天附件、实时录音、说话人区分、语音合成、知识库或具体云厂商。云端实现可替换 `IOcrProvider`、`IDocumentParser`、`ISpeechTranscriber`，复用任务与结果契约。

## 验证

```sh
dotnet build KaungAgent.sln
dotnet test KaungAgent.sln
# 已安装 requirements.lock 的 Python 3.11 环境
python -m pip install -r infra/content-engine/requirements-test.txt
cd infra/content-engine
python -m unittest test_processors -v
```

可通过 GitHub Actions 手动触发 `Content model acceptance (Linux CPU)`，或在模型就绪后运行 `python infra/content-engine/smoke.py --report /tmp/content-acceptance.json`。真实模型验收使用 `smoke.py`，读取固定样例并检查 OCR CER ≤ 5%、中文 ASR CER ≤ 10%、英文 ASR WER ≤ 15%，同时检查文字／扫描／混合 PDF、旧版 Word、Excel、字幕及时间戳。准确率只针对固定的清晰印刷体及清晰录音样本，不代表任意输入文件的保证。

引擎来源：[RapidOCR](https://github.com/RapidAI/RapidOCR)、[Docling](https://docling-project.github.io/docling/)、[faster-whisper](https://github.com/SYSTRAN/faster-whisper)、[LibreOffice 命令行](https://help.libreoffice.org/latest/en-US/text/shared/guide/start_parameters.html)。
