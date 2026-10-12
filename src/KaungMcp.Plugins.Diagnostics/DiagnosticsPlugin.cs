using KaungMcp.Plugin;
namespace KaungMcp.Plugins.Diagnostics;

public sealed class DiagnosticsPlugin : IKaungMcpPlugin
{
    public void Register(PluginRegistrationContext context)
    {
        context.CapabilityAssemblies.Add(GetType().Assembly);
        context.Tools.AddRange([new("kaung_ping", true), new("kaung_echo", true)]);
    }
    public Task InitializeAsync(PluginInitializationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
