namespace KaungMcp.Content;

public sealed class ContentOptions
{
    public long MaxFileBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxPdfPages { get; set; } = 200;
    public int MaxAudioMinutes { get; set; } = 120;
    public int JobTimeoutSeconds { get; set; } = 7200;
    public int RetentionDays { get; set; } = 7;
    public string EngineEndpoint { get; set; } = "http://127.0.0.1:8092";
    // Environment only; intentionally absent from editable setting definitions and API responses.
    public string EngineKey { get; set; } = "";
}
