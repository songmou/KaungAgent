namespace KaungMcp.Content;

public sealed class StoredContentFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string StorageKey { get; set; } = "";
    public string? PluginId { get; set; }
    public string OwnerKind { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public long CreatedTicks { get; set; }
    public long ExpiresTicks { get; set; }
}
