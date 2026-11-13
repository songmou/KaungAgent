using System.ComponentModel;
using System.Text.Json;
using KaungMcp.Plugin;
using KaungMcp.Tools;
using ModelContextProtocol.Server;

namespace KaungMcp.Plugins.Content;

[McpServerToolType, McpToolCategory("content", "内容处理", 30, "OCR、文档解析与音频转写；先上传文件或导入公开 URL，再提交并查询异步任务。")]
public sealed class ContentTools(IContentFileStore files, IPluginJobService jobs)
{
    [McpServerTool(Name = "kaung_file_import", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("从公开 HTTP(S) 下载链接导入一个文件，返回 fileId。也可先通过 POST /api/files 上传文件。禁止私网地址及服务器路径。")]
    public Task<ContentFileInfo> Import(string url, CancellationToken cancellationToken) => files.ImportAsync(url, cancellationToken);

    [McpServerTool(Name = "kaung_file_info", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("读取当前客户端文件的元信息和有效期。")]
    public Task<ContentFileInfo> Info(string fileId, CancellationToken cancellationToken) => files.GetAsync(fileId, cancellationToken);

    [McpServerTool(Name = "kaung_file_delete", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("删除当前客户端的文件；排队或运行中的任务使用的文件不能删除。")]
    public async Task<object> Delete(string fileId, CancellationToken cancellationToken)
    {
        await files.DeleteAsync(fileId, cancellationToken);
        return new { deleted = true };
    }
    [McpServerTool(Name = "kaung_ocr_recognize", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("提交中英文图片或 PDF OCR，立即返回 jobId。pages 例如 1-3,5；完成后获得文字、坐标和置信度。")]
    public async Task<object> Ocr(string fileId, CancellationToken cancellationToken, string pages = "") =>
        new { jobId = await jobs.SubmitJobAsync("content", "ocr", JsonSerializer.SerializeToElement(new { fileId, pages }), cancellationToken) };

    [McpServerTool(Name = "kaung_document_parse", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("异步解析 PDF、DOC/DOCX、XLS/XLSX、TXT/MD/CSV。ocrMode 为 auto/force/off；可指定 PDF 页范围、工作表和文本编码。返回 jobId。")]
    public async Task<object> Parse(string fileId, CancellationToken cancellationToken, string pages = "", string ocrMode = "auto",
        string[]? sheetNames = null, string encoding = "auto") =>
        new { jobId = await jobs.SubmitJobAsync("content", "document", JsonSerializer.SerializeToElement(new
        { fileId, pages, ocrMode, sheetNames = sheetNames ?? [], encoding }), cancellationToken) };

    [McpServerTool(Name = "kaung_audio_transcribe", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("异步将音频文件转为中英文文字和带时间戳字幕。language 为 auto/zh/en，wordTimestamps 启用词级时间戳。返回 jobId。")]
    public async Task<object> Transcribe(string fileId, CancellationToken cancellationToken, string language = "auto", bool wordTimestamps = false) =>
        new { jobId = await jobs.SubmitJobAsync("content", "speech", JsonSerializer.SerializeToElement(new
        { fileId, language, wordTimestamps }), cancellationToken) };

    [McpServerTool(Name = "kaung_content_job_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查询当前客户端内容处理任务；终态为 succeeded/failed/cancelled/timed_out/interrupted/skipped，成功时 result.files 包含结果 fileId。")]
    public Task<ContentJobInfo> Job(Guid jobId, CancellationToken cancellationToken) => jobs.GetJobAsync(jobId, cancellationToken);

    [McpServerTool(Name = "kaung_content_job_cancel", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("取消当前客户端的内容处理任务；运行中的任务会停止计算。")]
    public Task<ContentJobInfo> Cancel(Guid jobId, CancellationToken cancellationToken) => jobs.CancelJobAsync(jobId, cancellationToken);

    [McpServerTool(Name = "kaung_content_read", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("按字符偏移分段读取生成的 TXT/Markdown/JSON/CSV/字幕结果，默认 8000，最多 20000 字符；按 nextStartIndex 继续。")]
    public Task<ContentTextPage> Read(string fileId, CancellationToken cancellationToken, int startIndex = 0, int maxCharacters = 8000) =>
        files.ReadTextAsync(fileId, startIndex, maxCharacters, cancellationToken);
}
