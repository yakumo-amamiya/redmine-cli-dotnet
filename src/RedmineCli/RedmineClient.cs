using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>Query parameters in order. Null or empty values are left out of the URL.</summary>
internal sealed class Query : List<KeyValuePair<string, string?>>
{
    public Query()
    {
    }

    public Query(IEnumerable<KeyValuePair<string, string?>>? other)
        : base(other ?? [])
    {
    }

    /// <summary>For collection initializers: { "limit", 25 }. A key given twice keeps the last value.</summary>
    public void Add(string key, object? value) => Set(key, value);

    public void Set(string key, object? value)
    {
        RemoveAll(pair => pair.Key == key);
        base.Add(new KeyValuePair<string, string?>(key, value switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        }));
    }
}

/// <summary>
/// The Redmine REST API client.
/// - The key goes in the X-Redmine-API-Key header, and only to Redmine's own origin (redirects are followed here so that a
///   sign-in page on another host never sees it). The key is never logged.
/// - The proxy comes from HTTPS_PROXY / HTTP_PROXY / NO_PROXY as the Node version read them, else from Windows' proxy
///   settings (<see cref="EnvProxy"/>).
/// - Server certificates are checked against Windows' store, plus the roots of REDMINE_EXTRA_CA_CERTS.
/// - The client certificate (mTLS) is offered on a direct connection and through a proxy's CONNECT tunnel alike.
/// </summary>
internal sealed partial class RedmineClient : IDisposable
{
    private const string UserAgent = "redmine-cli-dotnet";
    private const int MaxRedirects = 10;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    internal const string ProxyHint =
        "社内プロキシ経由なら、環境変数 HTTPS_PROXY (例: http://proxy.example.co.jp:8080) と NO_PROXY を確認してください (設定しなければ Windows のプロキシ設定を使います)。認証付きなら http://user:pass@proxy:8080 の形式です。";

    private const string CertHint =
        "サーバーの証明書を検証できませんでした。プロキシが TLS を復号 (証明書を差し替え) している環境なら、社内ルート CA を Windows の証明書ストア (「信頼されたルート証明機関」) に入れるか、その PEM ファイルのパスを環境変数 REDMINE_EXTRA_CA_CERTS に設定してください。";

    private const string NameMismatchHint =
        "証明書の名前が .redmine.json の url のホスト名と一致しません。url には証明書に載っているホスト名を書いてください (IP アドレスでは一致しないことがあります)。";

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _apiKey;
    private readonly bool _verbose;
    private readonly ClientCertEnvNames _certEnv;
    private readonly ClientCertificate? _clientCertificate;
    private readonly X509Certificate2Collection _extraRoots;
    private readonly CancellationToken _cancellation;
    private string? _serverCertificateProblem;
    private bool _serverNameMismatchOnly;

    /// <param name="url">Redmine's root URL (a sub-path like https://host/redmine is fine).</param>
    /// <param name="certEnv">The variable names to show in hints; placeholders when null.</param>
    public RedmineClient(
        string url,
        string apiKey,
        ClientCertificate? clientCertificate,
        ClientCertEnvNames? certEnv,
        bool verbose,
        X509Certificate2Collection? extraRoots = null,
        CancellationToken cancellation = default)
    {
        BaseUrl = url.TrimEnd('/');
        _base = new Uri(BaseUrl + "/");
        _apiKey = apiKey;
        _verbose = verbose;
        _certEnv = certEnv ?? ClientCertEnvNames.Placeholder;
        _clientCertificate = clientCertificate;
        _extraRoots = extraRoots ?? [];
        _cancellation = cancellation;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        EnvProxy.Configure(handler);
        if (verbose)
        {
            Log($"経路: {EnvProxy.DescribeRoute(_base)}");
        }
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;
        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificateContext =
                SslStreamCertificateContext.Create(clientCertificate.Certificate, clientCertificate.Chain, offline: true);
        }
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public string BaseUrl { get; }

    public void Dispose()
    {
        _http.Dispose();
        _clientCertificate?.Dispose();
    }

    private void Log(string line)
    {
        if (_verbose)
        {
            Console.Error.Write($"[http] {line}\n");
        }
    }

    public Uri BuildUrl(string apiPath, Query? query = null)
    {
        var url = new Uri(_base, apiPath.TrimStart('/'));
        var pairs = (query ?? [])
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToList();
        if (pairs.Count == 0)
        {
            return url;
        }
        var builder = new UriBuilder(url);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0 ? string.Join("&", pairs) : existing + "&" + string.Join("&", pairs);
        return builder.Uri;
    }

    public Task<JsonNode?> GetAsync(string apiPath, Query? query = null) => RequestAsync("GET", apiPath, query);

    public Task<JsonNode?> PostAsync(string apiPath, JsonNode json, Query? query = null) => RequestAsync("POST", apiPath, query, json);

    public Task<JsonNode?> PutAsync(string apiPath, JsonNode json, Query? query = null) => RequestAsync("PUT", apiPath, query, json);

    /// <summary>Sends JSON and reads JSON. Null for 204 or an empty body. A status outside 2xx is an <see cref="HttpStatusException"/>.</summary>
    public async Task<JsonNode?> RequestAsync(string method, string apiPath, Query? query = null, JsonNode? json = null)
    {
        var url = BuildUrl(apiPath, query);
        Log($"> {method} {url.AbsoluteUri}");
        Func<HttpContent>? content = null;
        if (json is not null)
        {
            var body = Encoding.UTF8.GetBytes(Json.Serialize(json));
            content = () =>
            {
                var c = new ByteArrayContent(body);
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return c;
            };
        }
        var (response, finalUrl, redirected) = await SendAsync(method, url, content, "application/json");
        using (response)
        {
            var status = (int)response.StatusCode;
            Log($"< {status} {response.ReasonPhrase}{(redirected ? $" (リダイレクト後: {finalUrl.AbsoluteUri})" : "")}");
            var text = await ReadTextAsync(response);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpError(status, text, method, url);
            }
            if (status == 204 || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return Json.Parse(text);
            }
            catch (JsonException)
            {
                throw NotJsonError(response, text, method, url, finalUrl, redirected);
            }
        }
    }

    /// <summary>The object under key in a response, or exit code 5 when the server did not send one.</summary>
    public static JsonObject Expect(JsonNode? data, string key) =>
        data.Get(key) as JsonObject ?? throw new CliException($"サーバーの応答に {key} がありません", Exit.Http);

    /// <summary>
    /// Pages through a list API until max items. Redmine's lists answer { &lt;key&gt;: [...], total_count, offset, limit }.
    /// </summary>
    public async Task<(List<JsonNode> Items, long Total)> ListAllAsync(string apiPath, string key, Query? query = null, int max = int.MaxValue, int pageSize = 100)
    {
        var items = new List<JsonNode>();
        long offset = 0;
        long? total = null;
        while (items.Count < max)
        {
            var limit = Math.Min(pageSize, max - items.Count);
            var page = await GetAsync(apiPath, new Query(query) { { "offset", offset }, { "limit", limit } });
            var chunk = (page.Get(key) as JsonArray).Detach();
            items.AddRange(chunk);
            total = page.Get("total_count").Long() ?? items.Count;
            offset += chunk.Count;
            if (chunk.Count == 0 || offset >= total)
            {
                break;
            }
        }
        return (items, total ?? items.Count);
    }

    /// <summary>
    /// Sends a file to POST /uploads.json and returns the entry for an issue's uploads array (with the token).
    /// The length is sent up front (Content-Length), not chunked, for proxies that refuse chunked bodies.
    /// </summary>
    public async Task<JsonObject> UploadAsync(string filePath, string? description = null)
    {
        var abs = Path.GetFullPath(filePath);
        var info = new FileInfo(abs);
        if (!info.Exists)
        {
            throw new CliException(Directory.Exists(abs) ? $"ファイルではありません: {abs}" : $"ファイルが見つかりません: {abs}", Exit.Usage);
        }
        var filename = Path.GetFileName(abs);
        var url = BuildUrl("/uploads.json", new Query { { "filename", filename } });
        Log($"> POST {url.AbsoluteUri} ({info.Length} bytes)");
        HttpContent Content()
        {
            var c = new StreamContent(File.OpenRead(abs));
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            c.Headers.ContentLength = info.Length;
            return c;
        }
        var (response, finalUrl, redirected) = await SendAsync("POST", url, Content, "application/json");
        using (response)
        {
            var status = (int)response.StatusCode;
            Log($"< {status}");
            var text = await ReadTextAsync(response);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpError(status, text, "POST", url);
            }
            JsonNode? parsed;
            try
            {
                parsed = Json.Parse(text);
            }
            catch (JsonException)
            {
                throw NotJsonError(response, text, "POST", url, finalUrl, redirected);
            }
            var token = parsed.Get("upload").Get("token").Str();
            if (string.IsNullOrEmpty(token))
            {
                throw new CliException("アップロード応答にトークンがありません", Exit.Http);
            }
            var upload = new JsonObject { ["token"] = token, ["filename"] = filename, ["content_type"] = "application/octet-stream" };
            if (!string.IsNullOrEmpty(description))
            {
                upload["description"] = description;
            }
            return upload;
        }
    }

    /// <summary>Saves an attachment to destPath (through a .part file, so a broken download leaves no half file).</summary>
    public async Task DownloadAsync(long attachmentId, string filename, string destPath)
    {
        // content_url carries the host name of the server's settings, which may not resolve from inside; build it from url.
        var url = BuildUrl($"/attachments/download/{attachmentId}/{Uri.EscapeDataString(filename)}");
        Log($"> GET {url.AbsoluteUri}");
        var (response, _, _) = await SendAsync("GET", url, null, "*/*");
        using (response)
        {
            var status = (int)response.StatusCode;
            Log($"< {status}");
            if (!response.IsSuccessStatusCode)
            {
                throw HttpError(status, await ReadTextAsync(response), "GET", url);
            }
            var part = destPath + ".part";
            try
            {
                await using (var file = File.Create(part))
                {
                    await response.Content.CopyToAsync(file, _cancellation);
                }
                File.Move(part, destPath, overwrite: true);
            }
            catch
            {
                File.Delete(part);
                throw;
            }
        }
    }

    /// <summary>Sends one request, following redirects. The key is sent only while the URL stays on Redmine's origin.</summary>
    private async Task<(HttpResponseMessage Response, Uri FinalUrl, bool Redirected)> SendAsync(string method, Uri url, Func<HttpContent>? content, string accept)
    {
        var httpMethod = new HttpMethod(method);
        var redirected = false;
        for (var hop = 0; ; hop++)
        {
            _serverCertificateProblem = null;
            using var request = new HttpRequestMessage(httpMethod, url) { Content = content?.Invoke() };
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (SameOrigin(url, _base))
            {
                request.Headers.TryAddWithoutValidation("X-Redmine-API-Key", _apiKey);
            }
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancellation);
            }
            catch (HttpRequestException e)
            {
                throw NetworkError(e, method, url);
            }
            catch (TaskCanceledException) when (!_cancellation.IsCancellationRequested)
            {
                throw new CliException(
                    $"接続に失敗しました ({method} {url.Authority}): {RequestTimeout.TotalMinutes} 分待っても応答がありません", Exit.Error, ProxyHint);
            }
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                Log($"< {status} {response.ReasonPhrase} → {next.AbsoluteUri}");
                response.Dispose();
                if (status == 303 || (status is 301 or 302 && httpMethod == HttpMethod.Post))
                {
                    // As browsers do: the next request is a GET without the body.
                    httpMethod = HttpMethod.Get;
                    content = null;
                }
                url = next;
                redirected = true;
                continue;
            }
            return (response, url, redirected);
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    // Like fetch's res.text(): UTF-8 whatever the charset says (an unknown charset must not hide the server's message).
    private async Task<string> ReadTextAsync(HttpResponseMessage response)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(_cancellation);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        if (ExtraRoots.Accept(_extraRoots, certificate, chain, errors))
        {
            return true;
        }
        _serverNameMismatchOnly = errors == SslPolicyErrors.RemoteCertificateNameMismatch;
        _serverCertificateProblem = DescribeCertificateProblem(errors, chain);
        return false;
    }

    private static string DescribeCertificateProblem(SslPolicyErrors errors, X509Chain? chain)
    {
        var parts = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            parts.Add("サーバーが証明書を出しませんでした");
        }
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            parts.Add("証明書の名前が接続先のホスト名と一致しません");
        }
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var statuses = chain?.ChainStatus.Select(s => s.Status.ToString()).Distinct().ToList() ?? [];
            parts.Add(statuses.Count == 0 ? "証明書を信頼できません" : $"証明書を信頼できません ({string.Join(", ", statuses)})");
        }
        return string.Join("、", parts);
    }

    private CliException NetworkError(HttpRequestException error, string method, Uri url)
    {
        var env = _certEnv;
        var needCertHint =
            $"サーバーがクライアント証明書を要求している可能性があります (mTLS)。`redmine setup` を端末から実行して証明書を設定するか、証明書ファイルのパスを環境変数 {env.Cert} (PEM なら鍵を {env.Key}、PFX ならパスワードを {env.Password}) に設定してください。";
        var badCertHint =
            $"サーバーがクライアント証明書を拒否しました。証明書が正しいものか、有効期限が切れていないか、鍵とパスワード ({env.Password}) が合っているか確認してください。";
        var certHint = _clientCertificate is null ? needCertHint : badCertHint;
        var kind = error.HttpRequestError;
        string hint;
        if (_serverCertificateProblem is not null)
        {
            hint = _serverNameMismatchOnly ? NameMismatchHint : CertHint;
        }
        else if (kind == HttpRequestError.SecureConnectionError)
        {
            hint = certHint;
        }
        else if (url.Scheme == Uri.UriSchemeHttps && kind is HttpRequestError.ResponseEnded or HttpRequestError.Unknown && HasIOException(error))
        {
            // With TLS 1.3 a server that refuses the client certificate may just close the connection after the handshake;
            // the client sees nothing but the close.
            hint = $"TLS 接続がサーバー側で切られました。{certHint} プロキシ経由なら {ProxyHint}";
        }
        else
        {
            hint = ProxyHint;
        }
        var reason = Reason(error, kind);
        if (_serverCertificateProblem is { } problem)
        {
            reason += $" (サーバー証明書: {problem})";
        }
        return new CliException($"接続に失敗しました ({method} {url.Authority}): {reason}", Exit.Error, hint);
    }

    private static bool HasIOException(Exception error)
    {
        for (var e = error.InnerException; e is not null; e = e.InnerException)
        {
            if (e is IOException)
            {
                return true;
            }
        }
        return false;
    }

    private static string Reason(Exception error, HttpRequestError kind)
    {
        var innermost = error;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }
        var message = innermost.Message.Trim();
        return kind == HttpRequestError.Unknown ? message : $"{kind}: {message}";
    }

    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTitle();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("html", RegexOptions.IgnoreCase)]
    private static partial Regex Html();

    /// <summary>A 2xx that is not JSON: a wrong url, an SSO sign-in page, a proxy's block page.</summary>
    private CliException NotJsonError(HttpResponseMessage response, string text, string method, Uri url, Uri finalUrl, bool redirected)
    {
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "(不明)";
        var title = HtmlTitle().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        var head = Whitespace().Replace(text, " ");
        var lines = new List<string>
        {
            $"サーバーの応答が JSON ではありません ({method} {url.AbsoluteUri})",
            $"  ステータス: {(int)response.StatusCode}, Content-Type: {contentType}",
        };
        if (redirected)
        {
            lines.Add($"  リダイレクト後の URL: {finalUrl.AbsoluteUri}");
        }
        if (!string.IsNullOrEmpty(title))
        {
            lines.Add($"  HTML のタイトル: {title}");
        }
        lines.Add($"  先頭: {(head.Length > 160 ? head[..160] : head)}");
        string hint;
        if (redirected)
        {
            hint = "リダイレクトされています。SSO やプロキシのログインページに飛ばされている可能性が高いです。ブラウザでログインした状態で <url>/projects.json を開いて JSON が見えるか確認し、見えるなら経路 (HTTPS_PROXY / NO_PROXY、Windows のプロキシ設定) を疑ってください。";
        }
        else if (Html().IsMatch(contentType))
        {
            hint = "HTML が返っています。.redmine.json の url が Redmine のルートを指しているか確認してください (サブパス配置なら https://host/path のように Redmine の直上まで含める)。ブラウザで <url>/projects.json を開いて JSON が見えるかが判断基準です。";
        }
        else
        {
            hint = "ブラウザで <url>/projects.json を開いて JSON が返るか確認してください。--verbose で往復の詳細を表示できます。";
        }
        return new CliException(string.Join("\n", lines), Exit.Http, hint.Replace("<url>", BaseUrl));
    }

    private static HttpStatusException HttpError(int status, string text, string method, Uri url)
    {
        var details = new List<string>();
        try
        {
            if (Json.Parse(text).Get("errors") is JsonArray errors)
            {
                details.AddRange(errors.Select(e => e.Text()));
            }
        }
        catch (JsonException)
        {
            // not JSON
        }
        var where = $"{method} {url.AbsolutePath} -> HTTP {status}";
        string message;
        string? hint = null;
        switch (status)
        {
            case 401:
                message = $"認証に失敗しました ({where})";
                hint = "API キーの環境変数 (REDMINE_API_KEY_<識別子>、名前は `redmine target` で表示) の値が正しいか、Redmine の管理画面で REST API が有効になっているか確認してください。";
                break;
            case 403:
                message = $"権限がありません ({where})";
                hint = "このユーザーは対象プロジェクトでその操作を許可されていません。ロールと権限を確認してください。";
                break;
            case 404:
                message = $"見つかりません ({where})";
                hint = "id や識別子、または .redmine.json の url を確認してください。";
                break;
            case 407:
                message = $"プロキシ認証が必要です ({where})";
                hint = "HTTPS_PROXY に http://user:pass@proxy:port の形式で認証情報を含めてください。Windows の統合認証 (NTLM / Kerberos) のプロキシには、サインイン中のユーザーで応答します。";
                break;
            case 413:
                message = $"送信サイズが大きすぎます ({where})";
                hint = "Redmine の「添付ファイルサイズの上限」またはプロキシ側の上限を超えています。";
                break;
            case 422:
                message = $"サーバーが入力を拒否しました ({where})";
                if (details.Count == 0)
                {
                    hint = "必須項目の不足や、選べない値 (トラッカー、ステータス、担当者など) の指定が原因です。";
                }
                break;
            default:
                message = $"サーバーエラー ({where})";
                if (!string.IsNullOrEmpty(text) && details.Count == 0)
                {
                    var flat = Whitespace().Replace(text, " ");
                    message += $": {(flat.Length > 200 ? flat[..200] : flat)}";
                }
                break;
        }
        if (details.Count > 0)
        {
            message += "\n  - " + string.Join("\n  - ", details);
        }
        return new HttpStatusException(message, status, method, url.AbsoluteUri, details, hint);
    }

}
