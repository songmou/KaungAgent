using System.Text;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.Plugin;
using KaungMcp.Web;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Content;

public sealed class ContentFileStore(IDbContextFactory<KaungMcpDbContext> database,
    IPluginExecutionContextAccessor execution, ContentStateGate gate, IConfiguration configuration,
    ManagedSettingsStore settings, ISafeBinaryDownloader downloads, TimeProvider clock) : IContentFileStore
{
    private ContentOptions Options => settings.GetOptions<ContentOptions>("Content");
    private string Root => Path.GetFullPath(configuration["Content:FilesDirectory"] ??
        Path.Combine(Path.GetDirectoryName(ManagedSettingsStore.ResolvePath(configuration))!, "files"));
    private string PluginRoot => Path.GetFullPath(configuration["Plugins:DataDirectory"] ?? "data/plugins");
    private static ContentFileInfo View(StoredContentFile file) => new(file.Id, file.Name, file.MimeType, file.Size,
        new(file.CreatedTicks, TimeSpan.Zero), new(file.ExpiresTicks, TimeSpan.Zero), file.PluginId is not null);
    private string PhysicalPath(StoredContentFile file) => file.PluginId is null
        ? Path.Combine(Root, file.StorageKey)
        : Path.Combine(PluginRoot, file.PluginId, "artifacts", file.StorageKey);
    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl))
            throw new ArgumentException("文件名无效。");
        return name;
    }
    public async Task<ContentFileInfo> UploadAsync(Stream input, string name, CancellationToken ct) =>
        await SaveAsync(input, SafeName(name), null, null, ct);
    public async Task<ContentFileInfo> ImportAsync(string url, CancellationToken ct)
    {
        _ = execution.Current;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException("文件 URL 无效。");
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            BinaryDownloadInfo download;
            try
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                download = await downloads.DownloadAsync(uri, output, Options.MaxFileBytes, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("文件下载超时。"); }
            await using var input = File.OpenRead(temporary);
            return await UploadAsync(input, download.Name, ct);
        }
        finally { File.Delete(temporary); }
    }
    public Task<ContentFileInfo> CreateArtifactAsync(string pluginId, string name, string mimeType, Stream input, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pluginId, "^[a-z][a-z0-9-]{0,63}$")) throw new ArgumentException("插件 ID 无效。");
        if (mimeType is not ("text/plain" or "text/markdown" or "text/csv" or "application/json" or "application/x-subrip" or "text/vtt"))
            throw new ArgumentException("产物类型无效。");
        return SaveAsync(input, SafeName(name), pluginId, mimeType, ct);
    }
    private async Task<ContentFileInfo> SaveAsync(Stream source, string name, string? plugin, string? mime, CancellationToken ct)
    {
        var identity = execution.Current;
        var options = Options;
        var now = clock.GetUtcNow();
        var file = new StoredContentFile { Name = name, PluginId = plugin, OwnerKind = identity.Kind,
            OwnerId = identity.Id, CreatedTicks = now.UtcTicks, ExpiresTicks = now.AddDays(options.RetentionDays).UtcTicks };
        file.StorageKey = file.Id;
        var path = PhysicalPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".upload";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var buffer = new byte[65536];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    file.Size += read;
                    if (file.Size > options.MaxFileBytes) throw new ContentLimitException("文件超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            file.MimeType = mime ?? ContentFileValidation.Validate(temporary, name);
            await gate.Mutex.WaitAsync(ct);
            try
            {
                File.Move(temporary, path);
                await using var db = await database.CreateDbContextAsync(ct);
                db.ContentFiles.Add(file);
                await db.SaveChangesAsync(ct);
            }
            finally { gate.Mutex.Release(); }
            return View(file);
        }
        catch { File.Delete(path); throw; }
        finally { File.Delete(temporary); }
    }
    private async Task<StoredContentFile> ResolveAsync(string id, KaungMcpDbContext db, CancellationToken ct)
    {
        var identity = execution.Current;
        var file = await db.ContentFiles.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || !identity.IsAdministrator && (file.OwnerKind != identity.Kind || file.OwnerId != identity.Id))
            throw new KeyNotFoundException("文件不存在。");
        if (file.ExpiresTicks <= clock.GetUtcNow().UtcTicks && !await InUseAsync(db, id, ct))
            throw new KeyNotFoundException("文件已过期。");
        return file;
    }
    private static Task<bool> InUseAsync(KaungMcpDbContext db, string id, CancellationToken ct) =>
        db.TaskRuns.AnyAsync(r => r.InputFileId == id && (r.Status == "queued" || r.Status == "running"), ct);
    public async Task<ContentFileInfo> GetAsync(string fileId, CancellationToken ct)
    {
        await using var db = await database.CreateDbContextAsync(ct);
        return View(await ResolveAsync(fileId, db, ct));
    }
    public async Task<Stream> OpenReadAsync(string fileId, CancellationToken ct)
    {
        await gate.Mutex.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var file = await ResolveAsync(fileId, db, ct);
            return new FileStream(PhysicalPath(file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        finally { gate.Mutex.Release(); }
    }
    public async Task DeleteAsync(string fileId, CancellationToken ct)
    {
        await gate.Mutex.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var file = await ResolveAsync(fileId, db, ct);
            if (await InUseAsync(db, fileId, ct)) throw new ContentConflictException("文件正在被排队或运行中的任务使用。");
            File.Delete(PhysicalPath(file));
            db.ContentFiles.Remove(file);
            await db.SaveChangesAsync(ct);
        }
        finally { gate.Mutex.Release(); }
    }
    public async Task<ContentTextPage> ReadTextAsync(string fileId, int startIndex, int maxCharacters, CancellationToken ct)
    {
        if (startIndex < 0 || maxCharacters is < 1 or > 20000) throw new ArgumentException("读取范围无效；最多读取 20000 字符。");
        var file = await GetAsync(fileId, ct);
        if (!file.IsArtifact || !(file.MimeType.StartsWith("text/") || file.MimeType is "application/json" or "application/x-subrip"))
            throw new ArgumentException("只能分段读取已生成的文本类结果。");
        await using var stream = await OpenReadAsync(fileId, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // Count the whole stream without loading it into memory; offsets use .NET UTF-16 characters.
        var buffer = new char[8192];
        var content = new StringBuilder(maxCharacters);
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var begin = Math.Max(0, startIndex - total);
            var count = Math.Min(read - begin, maxCharacters - content.Length);
            if (count > 0) content.Append(buffer, begin, count);
            total += read;
        }
        if (startIndex > total) throw new ArgumentException("起始位置超出结果长度。");
        if (content.Length > 0 && char.IsLowSurrogate(content[0]))
            throw new ArgumentException("起始位置不能位于 Unicode 字符内部，请使用 nextStartIndex。");
        if (content.Length > 0 && char.IsHighSurrogate(content[^1]))
        {
            content.Length--;
            if (content.Length == 0) throw new ArgumentException("该 Unicode 字符需要至少 2 个字符的读取窗口。");
        }
        return new(fileId, content.ToString(), startIndex, content.Length, total,
            startIndex + content.Length < total ? startIndex + content.Length : null);
    }
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        if (!execution.Current.IsAdministrator) throw new UnauthorizedAccessException();
        await gate.Mutex.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow().UtcTicks;
            var expired = await db.ContentFiles.Where(f => f.ExpiresTicks <= now &&
                !db.TaskRuns.Any(r => r.InputFileId == f.Id && (r.Status == "queued" || r.Status == "running"))).ToListAsync(ct);
            foreach (var file in expired) { File.Delete(PhysicalPath(file)); db.ContentFiles.Remove(file); }
            await db.SaveChangesAsync(ct);
            return expired.Count;
        }
        finally { gate.Mutex.Release(); }
    }
}
