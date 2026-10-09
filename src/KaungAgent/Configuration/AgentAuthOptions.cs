namespace KaungAgent.Configuration;

public sealed class AgentAuthOptions
{
    public const string SectionName = "AgentAuth";

    public string ApiKey { get; set; } = string.Empty;
}
