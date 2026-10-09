using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KaungMcp.Data;
using KaungMcp.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Configuration;

public sealed class ManagedSettingsDocument
{
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? AdminKeyHash { get; set; }
}

public sealed record SettingsChange(Dictionary<string, string>? Changes, string[]? Clear);
public sealed record RotateAdminKeyRequest(string NewKey);

public sealed class ManagedSettingsStore
{
    public const string DefaultPath = ".kaung/mcp-settings.json";
    public const string DefaultCertificatePath = ".kaung/mcp-lan.pfx";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> RuntimeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "WebSearch:TimeoutSeconds", "WebSearch:SearXng:Enabled", "WebSearch:SearXng:BaseUrl",
        "WebSearch:StaticFallback:Enabled",
        "WebSearch:StaticFallback:BaseUrl", "WebSearch:StaticFallback:TimeoutSeconds",
        "WebSearch:StaticFallback:MaxResponseBytes", "WebSearch:StaticFallback:UserAgent",
        "WebFetch:TimeoutSeconds", "WebFetch:MaxResponseBytes", "WebFetch:MaxRedirects",
        "WebFetch:BrowserFallbackEnabled", "WebFetch:UserAgent", "WebFetch:RobotsCacheMinutes"
    };
    private static readonly HashSet<string> AllowedKeys = new(RuntimeKeys, StringComparer.OrdinalIgnoreCase)
    {
        "ConnectionStrings:KaungMcp", "AllowedHosts", "Logging:LogLevel:Default",
        "Logging:LogLevel:Microsoft.AspNetCore", "McpHost:LocalPort",
        "McpHost:LanEnabled", "McpHost:LanAddress", "McpHost:LanPort",
        "SearXngLauncher:Mode", "SearXngLauncher:Port", "SearXngLauncher:ExternalEndpoint"
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private readonly string _localEnvPath;
    private readonly string _certificatePath;
    private readonly Dictionary<string, string> _baseline;
    private readonly Dictionary<string, string> _startup;
    private ManagedSettingsDocument _document;
    private Dictionary<string, string> _effective;
    private long _version;

    public ManagedSettingsStore(IConfiguration configuration, Dictionary<string, string> baseline)
    {
        _settingsPath = Path.GetFullPath(configuration["McpManagement:SettingsPath"] ?? DefaultPath);
        _localEnvPath = Path.GetFullPath(configuration["McpManagement:LocalEnvPath"] ?? ".kaung/local.env");
        _certificatePath = Path.GetFullPath(configuration["McpManagement:CertificatePath"] ?? DefaultCertificatePath);
        _baseline = baseline;
        _document = Read(_settingsPath);
        _startup = Compose(_baseline, _document.Overrides);
        _effective = new(_startup, StringComparer.OrdinalIgnoreCase);
    }

    public static ManagedSettingsDocument Read(string path)
    {
        if (!File.Exists(path)) return new();
        var document = JsonSerializer.Deserialize<ManagedSettingsDocument>(File.ReadAllText(path), JsonOptions) ?? new();
        document.Overrides = new(document.Overrides, StringComparer.OrdinalIgnoreCase);
        return document;
    }

    public static string ResolvePath(IConfiguration configuration) =>
        Path.GetFullPath(configuration["McpManagement:SettingsPath"] ?? DefaultPath);

    public static Dictionary<string, string> CaptureBaseline(IConfiguration configuration) =>
        AllowedKeys.ToDictionary(key => key, key => configuration[key] ?? DefaultValue(key), StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Compose(Dictionary<string, string> baseline, Dictionary<string, string> overrides)
    {
        var values = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides) values[key] = value;
        // Launcher settings only replace the runtime search source when they were
        // explicitly managed; otherwise keep appsettings/environment precedence.
        if (!overrides.Keys.Any(key => key.StartsWith("SearXngLauncher:", StringComparison.OrdinalIgnoreCase)))
            return values;
        switch (values["SearXngLauncher:Mode"])
        {
            case "disabled":
                if (!overrides.ContainsKey("WebSearch:SearXng:Enabled")) values["WebSearch:SearXng:Enabled"] = "false";
                break;
            case "external":
                if (!overrides.ContainsKey("WebSearch:SearXng:Enabled")) values["WebSearch:SearXng:Enabled"] = "true";
                if (!overrides.ContainsKey("WebSearch:SearXng:BaseUrl"))
                    values["WebSearch:SearXng:BaseUrl"] = values["SearXngLauncher:ExternalEndpoint"];
                break;
            default:
                if (!overrides.ContainsKey("WebSearch:SearXng:Enabled")) values["WebSearch:SearXng:Enabled"] = "true";
                if (!overrides.ContainsKey("WebSearch:SearXng:BaseUrl"))
                    values["WebSearch:SearXng:BaseUrl"] = $"http://127.0.0.1:{values["SearXngLauncher:Port"]}";
                break;
        }
        return values;
    }

    public WebSearchOptions SearchOptions => Bind<WebSearchOptions>("WebSearch");
    public WebFetchOptions FetchOptions => Bind<WebFetchOptions>("WebFetch");
    public long Version => Interlocked.Read(ref _version);

    public object Snapshot()
    {
        var effective = new Dictionary<string, string>(_effective, StringComparer.OrdinalIgnoreCase);
        var desired = Compose(_baseline, _document.Overrides);
        var pending = AllowedKeys.Where(key => !RuntimeKeys.Contains(key) &&
            !string.Equals(_startup[key], desired[key], StringComparison.Ordinal)).ToArray();
        return new
        {
            effective,
            desired,
            overrides = _document.Overrides.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            pendingRestart = pending,
            certificate = CertificateInfo()
        };
    }

    public bool ValidateAdminKey(string supplied, string bootstrap)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var expected = _document.AdminKeyHash is { Length: 64 } hash
            ? Convert.FromHexString(hash)
            : SHA256.HashData(Encoding.UTF8.GetBytes(bootstrap));
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), expected);
    }

    public async Task<object> UpdateAsync(SettingsChange request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (overrides, proposed) = Prepare(request);
            await ValidateAsync(proposed, cancellationToken);
            var next = new ManagedSettingsDocument { Overrides = overrides, AdminKeyHash = _document.AdminKeyHash };
            if (bool.Parse(proposed["McpHost:LanEnabled"])) EnsureCertificate(proposed["McpHost:LanAddress"]);
            await PersistAsync(next, cancellationToken);
            var effective = new Dictionary<string, string>(_effective, StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> changedKeys = request.Changes?.Keys ?? Enumerable.Empty<string>();
            foreach (var key in changedKeys.Concat(request.Clear ?? []).Where(RuntimeKeys.Contains))
                effective[key] = proposed[key];
            _effective = effective;
            _document = next;
            Interlocked.Increment(ref _version);
            return Snapshot();
        }
        finally { _gate.Release(); }
    }

    public async Task<object> ValidateChangeAsync(SettingsChange request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (_, proposed) = Prepare(request);
            await ValidateAsync(proposed, cancellationToken);
            return new
            {
                valid = true,
                desired = proposed,
                pendingRestart = AllowedKeys.Where(key => !RuntimeKeys.Contains(key) &&
                    !string.Equals(_startup[key], proposed[key], StringComparison.Ordinal)).ToArray()
            };
        }
        finally { _gate.Release(); }
    }

    private (Dictionary<string, string> Overrides, Dictionary<string, string> Proposed) Prepare(SettingsChange request)
    {
        var overrides = new Dictionary<string, string>(_document.Overrides, StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Clear ?? [])
        {
            if (!AllowedKeys.Contains(key)) throw new ArgumentException($"Unknown setting: {key}");
            overrides.Remove(key);
        }
        foreach (var (key, value) in request.Changes ?? new())
        {
            if (!AllowedKeys.Contains(key)) throw new ArgumentException($"Unknown setting: {key}");
            if (value is null || value.Any(char.IsControl)) throw new ArgumentException($"Setting contains an invalid value: {key}");
            overrides[key] = value.Trim();
        }
        var proposed = Compose(_baseline, overrides);
        if (bool.TryParse(proposed["McpHost:LanEnabled"], out var enabled) && enabled)
        {
            var address = proposed["McpHost:LanAddress"];
            var allowedHosts = proposed["AllowedHosts"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!allowedHosts.Contains("*") && !allowedHosts.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                overrides["AllowedHosts"] = string.Join(';', allowedHosts.Append(address));
                proposed = Compose(_baseline, overrides);
            }
        }
        return (overrides, proposed);
    }

    public async Task RotateAdminKeyAsync(string newKey, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(newKey, "^[A-Za-z0-9._~+=/-]{32,}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("管理员密钥至少 32 位，且只能使用字母、数字及 ._~+=/- 字符。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = new ManagedSettingsDocument
            {
                Overrides = new(_document.Overrides, StringComparer.OrdinalIgnoreCase),
                AdminKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newKey)))
            };
            var oldEnv = File.Exists(_localEnvPath) ? await File.ReadAllTextAsync(_localEnvPath, cancellationToken) : null;
            if (oldEnv is not null)
            {
                var lines = oldEnv.Split('\n').ToList();
                var index = lines.FindIndex(line => line.StartsWith("KAUNG_MCP_ADMIN_KEY=", StringComparison.Ordinal));
                if (index < 0) throw new IOException("本地启动配置缺少管理员密钥，无法同步。");
                lines[index] = $"KAUNG_MCP_ADMIN_KEY={newKey}";
                await AtomicWriteAsync(_localEnvPath, string.Join('\n', lines), cancellationToken);
            }
            try { await PersistAsync(next, cancellationToken); }
            catch
            {
                if (oldEnv is not null) await AtomicWriteAsync(_localEnvPath, oldEnv, cancellationToken);
                throw;
            }
            _document = next;
        }
        finally { _gate.Release(); }
    }

    public byte[] PublicCertificate()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, null);
        return certificate.Export(X509ContentType.Cert);
    }

    private object? CertificateInfo()
    {
        if (!File.Exists(_certificatePath)) return null;
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, null);
        return new { thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)), notAfterUtc = certificate.NotAfter.ToUniversalTime() };
    }

    private void EnsureCertificate(string address)
    {
        if (File.Exists(_certificatePath))
        {
            using var existing = X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, null);
            if (existing.NotAfter > DateTime.UtcNow.AddDays(30) &&
                (existing.GetNameInfo(X509NameType.DnsName, false) == address ||
                existing.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                    .SelectMany(extension => extension.EnumerateIPAddresses())
                    .Any(ip => ip.ToString() == address))) return;
        }
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=KaungMcp LAN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(address));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var certificateDirectory = Path.GetDirectoryName(_certificatePath)!;
        var certificateDirectoryCreated = !Directory.Exists(certificateDirectory);
        Directory.CreateDirectory(certificateDirectory);
        if (certificateDirectoryCreated && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(certificateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _certificatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, certificate.Export(X509ContentType.Pkcs12));
            SecureFile(temporary);
            File.Move(temporary, _certificatePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ValidateAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        var search = Bind<WebSearchOptions>("WebSearch", values);
        var fetch = Bind<WebFetchOptions>("WebFetch", values);
        if (search.TimeoutSeconds is < 1 or > 60 ||
            !Uri.TryCreate(search.SearXng.BaseUrl, UriKind.Absolute, out var searxUri) ||
            searxUri.Scheme is not ("http" or "https") ||
            search.StaticFallback.TimeoutSeconds is < 1 or > 60 ||
            search.StaticFallback.MaxResponseBytes is < 65_536 or > 5_242_880 ||
            !Uri.TryCreate(search.StaticFallback.BaseUrl, UriKind.Absolute, out var staticUri) ||
            staticUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(staticUri.UserInfo) ||
            string.IsNullOrWhiteSpace(search.StaticFallback.UserAgent))
            throw new ArgumentException("网页搜索设置无效。");
        if (fetch.TimeoutSeconds is < 1 or > 60 || fetch.MaxResponseBytes is < 65_536 or > 10_485_760 ||
            fetch.MaxRedirects is < 0 or > 10 || fetch.RobotsCacheMinutes is < 1 or > 1440 ||
            string.IsNullOrWhiteSpace(fetch.UserAgent))
            throw new ArgumentException("网页读取设置无效。");
        if (!int.TryParse(values["McpHost:LocalPort"], out var localPort) || localPort is < 1 or > 65535 ||
            !int.TryParse(values["McpHost:LanPort"], out var lanPort) || lanPort is < 1 or > 65535)
            throw new ArgumentException("监听端口无效或重复。");
        if (!bool.TryParse(values["McpHost:LanEnabled"], out var lanEnabled))
            throw new ArgumentException("局域网开关无效。");
        if (lanEnabled && localPort == lanPort) throw new ArgumentException("本机与局域网监听端口不能相同。");
        if (lanEnabled && (!IPAddress.TryParse(values["McpHost:LanAddress"], out var lanIp) ||
            lanIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !IsPrivateIpv4(lanIp)))
            throw new ArgumentException("局域网地址必须是私有 IPv4 地址。");
        if (string.IsNullOrWhiteSpace(values["AllowedHosts"])) throw new ArgumentException("允许的主机不能为空。");
        if (!new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }.Contains(values["Logging:LogLevel:Default"]) ||
            !new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }.Contains(values["Logging:LogLevel:Microsoft.AspNetCore"]))
            throw new ArgumentException("日志级别无效。");
        var mode = values["SearXngLauncher:Mode"];
        if (mode is not ("local" or "external" or "disabled") ||
            !int.TryParse(values["SearXngLauncher:Port"], out var searxPort) || searxPort is < 1 or > 65535 ||
            mode == "external" && (!Uri.TryCreate(values["SearXngLauncher:ExternalEndpoint"], UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")))
            throw new ArgumentException("SearXNG 启动设置无效。");
        SqliteConnectionStringBuilder builder;
        string currentDataSource;
        try
        {
            builder = new SqliteConnectionStringBuilder(values["ConnectionStrings:KaungMcp"]);
            if (builder.Mode is SqliteOpenMode.ReadOnly or SqliteOpenMode.Memory)
                throw new ArgumentException("数据库连接必须允许写入现有文件。");
            builder.Mode = SqliteOpenMode.ReadOnly;
            currentDataSource = new SqliteConnectionStringBuilder(_startup["ConnectionStrings:KaungMcp"]).DataSource;
            if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
                throw new ArgumentException("必须指定已存在的 SQLite 数据库文件。");
        }
        catch (ArgumentException exception) when (exception.ParamName is null)
        {
            throw new ArgumentException("数据库连接字符串无效。", exception);
        }
        if (!string.Equals(Path.GetFullPath(builder.DataSource), Path.GetFullPath(currentDataSource), StringComparison.Ordinal))
        {
            if (!File.Exists(builder.DataSource)) throw new ArgumentException("目标数据库文件不存在。");
            try
            {
                await using var connection = new SqliteConnection(builder.ToString());
                await connection.OpenAsync(cancellationToken);
                foreach (var table in new[] { "McpClientApplications", "McpInvocationLogs", "__EFMigrationsHistory" })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name";
                    command.Parameters.AddWithValue("$name", table);
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                        throw new ArgumentException("目标数据库结构与 KaungMcp 不兼容。");
                }
                await using var history = connection.CreateCommand();
                history.CommandText = "SELECT count(*) FROM __EFMigrationsHistory WHERE MigrationId='20260919150010_InitialCreate'";
                if (Convert.ToInt32(await history.ExecuteScalarAsync(cancellationToken)) != 1)
                    throw new ArgumentException("目标数据库缺少 KaungMcp 初始迁移。");
            }
            catch (SqliteException exception) { throw new ArgumentException("目标数据库无法读取或结构不兼容。", exception); }
        }
    }

    private T Bind<T>(string section) where T : new() => Bind<T>(section, _effective);
    private static T Bind<T>(string section, Dictionary<string, string> values) where T : new()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values.Select(x =>
            new KeyValuePair<string, string?>(x.Key, x.Value))).Build();
        return config.GetSection(section).Get<T>() ?? new T();
    }

    private async Task PersistAsync(ManagedSettingsDocument document, CancellationToken ct) =>
        await AtomicWriteAsync(_settingsPath, JsonSerializer.Serialize(document, JsonOptions), ct);

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        var directoryCreated = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (directoryCreated && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            SecureFile(temporary);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    private static string DefaultValue(string key) => key switch
    {
        "WebSearch:TimeoutSeconds" => "10",
        "WebSearch:SearXng:Enabled" => "true",
        "WebSearch:SearXng:BaseUrl" => "http://127.0.0.1:8080",
        "WebSearch:StaticFallback:Enabled" => "true",
        "WebSearch:StaticFallback:BaseUrl" => "https://www.bing.com/search",
        "WebSearch:StaticFallback:TimeoutSeconds" => "12",
        "WebSearch:StaticFallback:MaxResponseBytes" => "2097152",
        "WebSearch:StaticFallback:UserAgent" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/151 Safari/537.36",
        "WebFetch:TimeoutSeconds" => "15",
        "WebFetch:MaxResponseBytes" => "2097152",
        "WebFetch:MaxRedirects" => "5",
        "WebFetch:BrowserFallbackEnabled" => "true",
        "WebFetch:UserAgent" => "KaungMcpBot/1.0 (Autonomous web fetcher)",
        "WebFetch:RobotsCacheMinutes" => "30",
        "ConnectionStrings:KaungMcp" => "Data Source=data/kaungmcp.db;Cache=Shared;Default Timeout=30",
        "AllowedHosts" => "localhost;127.0.0.1",
        "Logging:LogLevel:Default" => "Information",
        "Logging:LogLevel:Microsoft.AspNetCore" => "Warning",
        "McpHost:LocalPort" => "5091", "McpHost:LanEnabled" => "false",
        "McpHost:LanAddress" => "192.168.1.10", "McpHost:LanPort" => "5443",
        "SearXngLauncher:Mode" => "local", "SearXngLauncher:Port" => "8080",
        "SearXngLauncher:ExternalEndpoint" => "", _ => ""
    };
}
