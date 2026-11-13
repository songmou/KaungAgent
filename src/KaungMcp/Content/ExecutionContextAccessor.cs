using KaungMcp.Plugin;
using KaungMcp.Security;

namespace KaungMcp.Content;

public sealed class ExecutionContextAccessor(IHttpContextAccessor http) : IPluginExecutionContextAccessor
{
    internal const string SubmittedJobCorrelationKey = "kaung.content.job_correlation";
    private readonly AsyncLocal<ExecutionIdentity?> current = new();
    public ExecutionIdentity Current
    {
        get
        {
            if (current.Value is { } identity) return identity;
            var user = http.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                if (user.Identity.AuthenticationType == SecurityConstants.AdminScheme) return new("admin", "administrator");
                if (Guid.TryParse(user.FindFirst(SecurityConstants.ClientIdClaim)?.Value, out var id)) return new("client", id.ToString());
            }
            throw new UnauthorizedAccessException("需要已鉴权的文件或任务身份。");
        }
    }
    public IDisposable Push(ExecutionIdentity identity)
    {
        var previous = current.Value;
        current.Value = identity;
        return new Restore(() => current.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

// Serializes file deletion/expiry with persisted job submission and artifact publication.
public sealed class ContentStateGate
{
    public SemaphoreSlim Mutex { get; } = new(1, 1);
}
