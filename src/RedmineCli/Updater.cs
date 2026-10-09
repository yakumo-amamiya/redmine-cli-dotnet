using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>
/// Self-update from the GitHub Releases: finds the latest version, downloads the zip and checks it against SHA256SUMS, and
/// swaps the exe in. The latest version is read from github.com's redirect (/releases/latest → /releases/tag/vX.Y.Z), not
/// from api.github.com, whose limit per address a company's shared address reaches quickly.
/// </summary>
internal sealed partial class Updater : IDisposable
{
    public const string DefaultReleasesUrl = "https://github.com/yakumo-amamiya/redmine-cli-dotnet/releases";

    /// <summary>Points the update at another releases URL (the tests' mock server).</summary>
    public const string ReleasesUrlEnv = "REDMINE_CLI_RELEASES_URL";

    public const string Asset = "redmine-win-x64.zip";
    private const string ExeName = "redmine.exe";

    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect;
    private readonly string _releases;
    private readonly bool _verbose;
    private readonly CancellationToken _cancellation;

    public Updater(string? releasesUrl, bool verbose, X509Certificate2Collection extraRoots, CancellationToken cancellation)
    {
        _releases = (string.IsNullOrEmpty(releasesUrl) ? DefaultReleasesUrl : releasesUrl).TrimEnd('/');
        _verbose = verbose;
        _cancellation = cancellation;
        _http = new HttpClient(Handler(extraRoots, redirect: true)) { Timeout = TimeSpan.FromMinutes(5) };
        _noRedirect = new HttpClient(Handler(extraRoots, redirect: false)) { Timeout = TimeSpan.FromMinutes(1) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "redmine-cli-dotnet");
        _noRedirect.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "redmine-cli-dotnet");
    }

    // The same proxy and trust as the Redmine client: HTTPS_PROXY or Windows' settings, Windows' store plus REDMINE_EXTRA_CA_CERTS.
    private static SocketsHttpHandler Handler(X509Certificate2Collection extraRoots, bool redirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = redirect,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        EnvProxy.Configure(handler);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => ExtraRoots.Accept(extraRoots, certificate, chain, errors);
        return handler;
    }

    public void Dispose()
    {
        _http.Dispose();
        _noRedirect.Dispose();
    }

    /// <summary>The version of this exe ("0.2.0").</summary>
    public static string CurrentVersion =>
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Whether version a is newer than b; when either is not x.y.z, whether they differ.</summary>
    public static bool IsNewer(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va > vb : !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Log(string line)
    {
        if (_verbose)
        {
            Console.Error.Write($"[http] {line}\n");
        }
    }

    [GeneratedRegex("/tag/v?([^/]+)$")]
    private static partial Regex TagInPath();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)$")]
    private static partial Regex SumLine();

    /// <summary>The latest released version, from where /releases/latest redirects to.</summary>
    public async Task<string> LatestVersionAsync()
    {
        var url = new Uri($"{_releases}/latest");
        using var response = await SendAsync(_noRedirect, url);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (TagInPath().Match(target.AbsolutePath) is { Success: true } m)
            {
                return Uri.UnescapeDataString(m.Groups[1].Value);
            }
        }
        throw new CliException($"最新のリリースが分かりません ({url.AbsoluteUri} -> HTTP {(int)response.StatusCode})", Exit.Error,
            "リリースのページをブラウザで開けるか確認してください。");
    }

    /// <summary>Downloads the version's zip and SHA256SUMS into workDir, checks the hash and extracts redmine.exe; returns its path.</summary>
    public async Task<string> DownloadAsync(string version, string workDir)
    {
        var baseUrl = $"{_releases}/download/v{version}";
        var zip = Path.Combine(workDir, Asset);
        using (var response = await GetOkAsync(new Uri($"{baseUrl}/{Asset}")))
        {
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file, _cancellation);
        }
        string sums;
        using (var response = await GetOkAsync(new Uri($"{baseUrl}/SHA256SUMS")))
        {
            sums = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(_cancellation));
        }
        var expected = sums.Split('\n')
            .Select(line => SumLine().Match(line.Trim()))
            .FirstOrDefault(m => m.Success && m.Groups[2].Value == Asset)?.Groups[1].Value
            ?? throw new CliException($"SHA256SUMS に {Asset} がありません", Exit.Error);
        string actual;
        await using (var stream = File.OpenRead(zip))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, _cancellation));
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"落とした {Asset} のハッシュが SHA256SUMS と一致しません。置き換えずに中止しました", Exit.Error);
        }
        var exe = Path.Combine(workDir, ExeName);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.FullName == ExeName)
                ?? throw new CliException($"{Asset} に {ExeName} が入っていません", Exit.Error);
            entry.ExtractToFile(exe, overwrite: true);
        }
        return exe;
    }

    private async Task<HttpResponseMessage> GetOkAsync(Uri url)
    {
        var response = await SendAsync(_http, url);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new CliException($"落とせませんでした ({url.AbsoluteUri} -> HTTP {status})", Exit.Error,
                status == 404 ? "その版のリリースがありません。--to の版を確認してください。" : RedmineClient.ProxyHint);
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri url)
    {
        Log($"> GET {url.AbsoluteUri}");
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cancellation);
        }
        catch (HttpRequestException e)
        {
            var innermost = (Exception)e;
            while (innermost.InnerException is { } inner)
            {
                innermost = inner;
            }
            throw new CliException($"{url.Authority} に接続できません: {innermost.Message.Trim()}", Exit.Error,
                $"更新は github.com から落とします。{RedmineClient.ProxyHint}");
        }
        catch (TaskCanceledException) when (!_cancellation.IsCancellationRequested)
        {
            throw new CliException($"{url.Authority} から応答がありません", Exit.Error, RedmineClient.ProxyHint);
        }
        Log($"< {(int)response.StatusCode} {response.ReasonPhrase}");
        return response;
    }

    /// <summary>
    /// Puts newExe at target. It is first copied beside target as redmine.new.exe and checked with verify there (the folder
    /// the user runs redmine from, where a policy that blocks programs in %TEMP% does not apply); a failure leaves target as
    /// it was. Then the current exe is moved to target.old (a running exe cannot be overwritten or deleted on Windows, but it
    /// can be renamed; the next start of redmine deletes it) and the new one takes its name.
    /// Nothing after the swap may load code the running exe has not loaded yet: its file is no longer at its path.
    /// </summary>
    public static void Install(string target, string newExe, Func<string, bool>? verify)
    {
        var staged = Path.Combine(Path.GetDirectoryName(target)!, "redmine.new.exe");
        File.Copy(newExe, staged, overwrite: true);
        try
        {
            if (verify is not null && !verify(staged))
            {
                throw new CliException("落とした exe が動きませんでした。置き換えずに中止しました", Exit.Error,
                    "ウイルス対策ソフトやアプリの実行制限に止められていないか確認してください。install.ps1 で入れ直すこともできます。");
            }
            var old = target + ".old";
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An older redmine started from target.old is still running; leave it and use another name.
                old = $"{target}.{Guid.NewGuid():N}.old";
            }
            File.Move(target, old);
            try
            {
                File.Move(staged, target);
            }
            catch
            {
                File.Move(old, target);
                throw;
            }
        }
        finally
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the next update to overwrite.
            }
        }
    }

    /// <summary>Deletes what the last update left (best effort; called at every start).</summary>
    public static void CleanUpOld()
    {
        if (Environment.ProcessPath is not { } self)
        {
            return;
        }
        try
        {
            File.Delete(self + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still running (an update in progress), or not ours to delete.
        }
    }
}
