using System.Text.Json;

namespace KaungMcp.Plugin;

/// <summary>Established by the authenticated host, never accepted from tool arguments.</summary>
public sealed record ExecutionIdentity(string Kind, string Id)
{
    public bool IsAdministrator => Kind is "admin" or "system";
}

public interface IPluginExecutionContextAccessor
{
    ExecutionIdentity Current { get; }
    IDisposable Push(ExecutionIdentity identity);
}

public sealed record ContentFileInfo(string FileId, string Name, string MimeType, long Size,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool IsArtifact);
public sealed record ContentTextPage(string FileId, string Content, int StartIndex,
    int ReturnedCharacters, int TotalCharacters, int? NextStartIndex);

public interface IContentFileStore
{
    Task<ContentFileInfo> UploadAsync(Stream input, string name, CancellationToken ct);
    Task<ContentFileInfo> ImportAsync(string url, CancellationToken ct);
    Task<ContentFileInfo> GetAsync(string fileId, CancellationToken ct);
    Task<Stream> OpenReadAsync(string fileId, CancellationToken ct);
    Task DeleteAsync(string fileId, CancellationToken ct);
    Task<ContentTextPage> ReadTextAsync(string fileId, int startIndex, int maxCharacters, CancellationToken ct);
    Task<ContentFileInfo> CreateArtifactAsync(string pluginId, string name, string mimeType, Stream input, CancellationToken ct);
    Task<int> CleanupAsync(CancellationToken ct);
}

public sealed record ContentJobInfo(Guid JobId, string Status, string? Summary, string? Error,
    JsonElement? Result, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, bool CancelRequested);

public interface IPluginJobService
{
    Task<Guid> SubmitJobAsync(string pluginId, string handlerId, JsonElement arguments, CancellationToken ct);
    Task<ContentJobInfo> GetJobAsync(Guid jobId, CancellationToken ct);
    Task<ContentJobInfo> CancelJobAsync(Guid jobId, CancellationToken ct);
}

public sealed class ContentConflictException(string message) : Exception(message);
public sealed class ContentLimitException(string message) : Exception(message);
