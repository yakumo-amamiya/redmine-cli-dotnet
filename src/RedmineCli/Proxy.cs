using System.Net;

namespace RedmineCli;

/// <summary>
/// The proxy, chosen from the variables as the Node version chose it (undici's EnvHttpProxyAgent):
/// - an https URL goes through https_proxy / HTTPS_PROXY, or http_proxy / HTTP_PROXY when that is not set;
///   an http URL goes through http_proxy / HTTP_PROXY;
/// - no_proxy / NO_PROXY lists hosts to reach directly, separated by commas or spaces: "*" is every host, "host" that host
///   only, ".example.co.jp" or "*.example.co.jp" every host name ending with it, "host:443" that host on that port only;
/// - user:pass in the proxy URL answers the proxy's Basic challenge; a proxy that asks for Windows sign-in (NTLM /
///   Kerberos) gets the signed-in user's credentials.
/// When none of the proxy variables is set, Windows' proxy settings are used (.NET's default proxy), which the Node
/// version did not read.
/// </summary>
internal sealed class EnvProxy : IWebProxy
{
    private readonly Uri? _http;
    private readonly Uri? _https;
    private readonly string? _httpVariable;
    private readonly string? _httpsVariable;
    private readonly bool _noProxyAll;
    private readonly List<(string Host, int Port)> _noProxy = [];

    private EnvProxy(Uri? http, string? httpVariable, Uri? https, string? httpsVariable, string? noProxy)
    {
        _http = http;
        _httpVariable = httpVariable;
        _https = https;
        _httpsVariable = httpsVariable;
        var entries = (noProxy ?? "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        _noProxyAll = noProxy?.Trim() == "*";
        foreach (var entry in entries)
        {
            var colon = entry.LastIndexOf(':');
            if (colon > 0 && int.TryParse(entry[(colon + 1)..], out var port) && !entry.EndsWith(']'))
            {
                _noProxy.Add((entry[..colon].ToLowerInvariant(), port));
            }
            else
            {
                _noProxy.Add((entry.ToLowerInvariant(), 0));
            }
        }
        Credentials = new UserInfoCredentials(http, https);
    }

    public ICredentials? Credentials { get; set; }

    /// <summary>The proxy of the variables, or null when no proxy variable is set (then .NET's default: Windows' settings).</summary>
    public static EnvProxy? FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var (http, httpVariable) = Read(getEnv, "http_proxy", "HTTP_PROXY");
        var (https, httpsVariable) = Read(getEnv, "https_proxy", "HTTPS_PROXY");
        if (http is null && https is null)
        {
            return null;
        }
        var noProxy = First(getEnv, "no_proxy", "NO_PROXY").Value;
        return new EnvProxy(http, httpVariable, https ?? http, https is not null ? httpsVariable : httpVariable, noProxy);
    }

    private static (string? Name, string? Value) First(Func<string, string?> getEnv, params string[] names)
    {
        foreach (var name in names)
        {
            if (getEnv(name)?.Trim() is { Length: > 0 } value)
            {
                return (name, value);
            }
        }
        return (null, null);
    }

    private static (Uri? Url, string? Variable) Read(Func<string, string?> getEnv, string lower, string upper)
    {
        var (name, value) = First(getEnv, lower, upper);
        if (name is null || value is null)
        {
            return (null, null);
        }
        // Shown as the upper-case name, the one the documents use (on Windows the two are one variable anyway).
        var shown = upper;
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Host.Length == 0)
        {
            // The value is not printed: it may hold a password.
            throw new CliException($"環境変数 {shown} の値を URL として読めません", Exit.Config,
                "http://proxy.example.co.jp:8080 (認証付きなら http://user:pass@proxy.example.co.jp:8080) の形で指定してください。");
        }
        return (url, shown);
    }

    /// <summary>Points the handler at the variables' proxy, or leaves it on .NET's default (Windows' settings).</summary>
    public static void Configure(SocketsHttpHandler handler, Func<string, string?>? getEnv = null)
    {
        handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        if (FromEnvironment(getEnv) is { } proxy)
        {
            handler.UseProxy = true;
            handler.Proxy = proxy;
        }
    }

    private Uri? For(Uri destination) => destination.Scheme == Uri.UriSchemeHttps ? _https : _http;

    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : WithoutUserInfo(For(destination)!);

    public bool IsBypassed(Uri host) => For(host) is null || !ShouldProxy(host.IdnHost.ToLowerInvariant(), host.Port);

    // undici's rule: "host" must match exactly; ".host" and "*.host" match the end of the name; a port, when given, must match.
    private bool ShouldProxy(string host, int port)
    {
        if (_noProxyAll)
        {
            return false;
        }
        foreach (var (entry, entryPort) in _noProxy)
        {
            if (entryPort != 0 && entryPort != port)
            {
                continue;
            }
            if (entry.StartsWith('.') || entry.StartsWith('*'))
            {
                if (host.EndsWith(entry.TrimStart('*'), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (host == entry)
            {
                return false;
            }
        }
        return true;
    }

    private static Uri WithoutUserInfo(Uri url) =>
        url.UserInfo.Length == 0 ? url : new UriBuilder(url) { UserName = "", Password = "" }.Uri;

    /// <summary>How a request to url leaves this PC, for doctor and --verbose. Never shows the proxy's password.</summary>
    public static string DescribeRoute(Uri url, Func<string, string?>? getEnv = null)
    {
        if (FromEnvironment(getEnv) is { } proxy)
        {
            if (proxy.IsBypassed(url))
            {
                return "直接接続 (環境変数 NO_PROXY に該当)";
            }
            var via = proxy.GetProxy(url)!;
            var variable = url.Scheme == Uri.UriSchemeHttps ? proxy._httpsVariable : proxy._httpVariable;
            var why = url.Scheme == Uri.UriSchemeHttps && variable == "HTTP_PROXY" ? "。HTTPS_PROXY が無いので" : "";
            return $"プロキシ {via.Scheme}://{via.Authority} 経由 (環境変数 {variable}{why})";
        }
        var system = HttpClient.DefaultProxy;
        var systemVia = system.IsBypassed(url) ? null : system.GetProxy(url);
        return systemVia is null || systemVia == url
            ? "直接接続 (プロキシの環境変数も Windows のプロキシ設定も無い)"
            : $"プロキシ {systemVia.Scheme}://{systemVia.Authority} 経由 (Windows のプロキシ設定)";
    }

    /// <summary>The user:pass of each proxy URL for its Basic challenge; the signed-in user for NTLM / Kerberos.</summary>
    private sealed class UserInfoCredentials : ICredentials
    {
        private readonly Dictionary<string, NetworkCredential> _byProxy = new(StringComparer.OrdinalIgnoreCase);

        public UserInfoCredentials(params Uri?[] proxies)
        {
            foreach (var proxy in proxies)
            {
                if (proxy is not null && proxy.UserInfo.Length > 0)
                {
                    var colon = proxy.UserInfo.IndexOf(':');
                    var user = colon < 0 ? proxy.UserInfo : proxy.UserInfo[..colon];
                    var password = colon < 0 ? "" : proxy.UserInfo[(colon + 1)..];
                    _byProxy[proxy.Authority] = new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(password));
                }
            }
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (_byProxy.TryGetValue(uri.Authority, out var credential))
            {
                return credential;
            }
            return authType.Equals("Negotiate", StringComparison.OrdinalIgnoreCase) || authType.Equals("NTLM", StringComparison.OrdinalIgnoreCase)
                ? CredentialCache.DefaultNetworkCredentials
                : null;
        }
    }
}
