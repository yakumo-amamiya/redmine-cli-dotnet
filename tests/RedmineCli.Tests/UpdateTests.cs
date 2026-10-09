using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>A mock of the GitHub Releases pages that redmine update reads (/releases/latest and /releases/download/...).</summary>
public sealed class ReleasesFixture : IAsyncLifetime
{
    public const string Latest = "9.9.9";

    public MockRedmine Mock { get; private set; } = null!;

    public string ReleasesUrl => $"{Mock.BaseUrl}/releases";

    public async Task InitializeAsync()
    {
        Mock = await MockRedmine.StartAsync();
        Reset();
    }

    public Task DisposeAsync() => Mock.DisposeAsync().AsTask();

    public static byte[] Zip(string entryName, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(entryName).Open();
            entry.Write(content);
        }
        return buffer.ToArray();
    }

    public static string Sums(byte[] zip) => $"{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}  redmine-win-x64.zip\n";

    /// <summary>Latest is 9.9.9, whose zip holds a redmine.exe with the text "new exe".</summary>
    public void Reset(byte[]? zip = null, string? sums = null)
    {
        Mock.Routes.Clear();
        Mock.ClearRequests();
        zip ??= Zip("redmine.exe", Encoding.UTF8.GetBytes("new exe"));
        sums ??= Sums(zip);
        Mock.Route("GET /releases/latest", _ => new MockResponse(302,
            Headers: new Dictionary<string, string> { ["location"] = $"{Mock.BaseUrl}/releases/tag/v{Latest}" }));
        Mock.Route($"GET /releases/download/v{Latest}/redmine-win-x64.zip", _ => new MockResponse(Bytes: zip));
        Mock.Route($"GET /releases/download/v{Latest}/SHA256SUMS", _ => new MockResponse(Body: sums));
    }
}

/// <summary>redmine update: finding the latest version, checking the download, swapping the exe (in a temp folder, never the real one).</summary>
public sealed class UpdateTests : IClassFixture<ReleasesFixture>
{
    private readonly ReleasesFixture _f;

    public UpdateTests(ReleasesFixture fixture)
    {
        _f = fixture;
        _f.Reset();
    }

    private Updater NewUpdater() => new(_f.ReleasesUrl, verbose: false, [], CancellationToken.None);

    [Fact]
    public async Task The_latest_version_comes_from_the_redirect_of_releases_latest()
    {
        using var updater = NewUpdater();
        Assert.Equal(ReleasesFixture.Latest, await updater.LatestVersionAsync());
    }

    [Fact]
    public async Task The_download_is_checked_against_SHA256SUMS_and_the_exe_extracted()
    {
        using var updater = NewUpdater();
        var work = TempDir("redmine-update-");
        var exe = await updater.DownloadAsync(ReleasesFixture.Latest, work);
        Assert.Equal("new exe", File.ReadAllText(exe));
    }

    [Fact]
    public async Task A_hash_that_does_not_match_stops_the_update()
    {
        _f.Reset(sums: $"{new string('0', 64)}  redmine-win-x64.zip\n");
        using var updater = NewUpdater();
        var e = await Assert.ThrowsAnyAsync<CliException>(() => updater.DownloadAsync(ReleasesFixture.Latest, TempDir("redmine-update-")));
        Assert.Contains("一致しません", e.Message);
    }

    [Fact]
    public async Task A_missing_version_says_so()
    {
        using var updater = NewUpdater();
        var e = await Assert.ThrowsAnyAsync<CliException>(() => updater.DownloadAsync("0.0.1", TempDir("redmine-update-")));
        Assert.Contains("--to", e.Hint);
    }

    [Fact]
    public void Install_checks_the_new_exe_beside_the_target_then_moves_the_old_one_aside()
    {
        var dir = TempDir("redmine-replace-");
        var target = Path.Combine(dir, "redmine.exe");
        var fresh = Path.Combine(TempDir("redmine-new-"), "redmine.exe");
        File.WriteAllText(target, "old exe");
        File.WriteAllText(fresh, "new exe");
        string? checkedAt = null;
        Updater.Install(target, fresh, path =>
        {
            checkedAt = path;
            return File.ReadAllText(path) == "new exe";
        });
        Assert.Equal(Path.Combine(dir, "redmine.new.exe"), checkedAt);
        Assert.Equal("new exe", File.ReadAllText(target));
        Assert.Equal("old exe", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(checkedAt));
    }

    [Fact]
    public void Install_leaves_the_target_alone_when_the_new_exe_does_not_run()
    {
        var dir = TempDir("redmine-replace-");
        var target = Path.Combine(dir, "redmine.exe");
        var fresh = Path.Combine(TempDir("redmine-new-"), "redmine.exe");
        File.WriteAllText(target, "old exe");
        File.WriteAllText(fresh, "broken exe");
        var e = Assert.ThrowsAny<CliException>(() => Updater.Install(target, fresh, _ => false));
        Assert.Contains("置き換えずに中止しました", e.Message);
        Assert.Equal("old exe", File.ReadAllText(target));
        Assert.Equal(["redmine.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Versions_compare_as_numbers()
    {
        Assert.True(Updater.IsNewer("0.10.0", "0.9.0"));
        Assert.False(Updater.IsNewer("0.2.0", "0.2.0"));
        Assert.False(Updater.IsNewer("0.1.0", "0.2.0"));
    }

    [Fact]
    public async Task Update_check_reports_a_newer_version_without_replacing()
    {
        var r = await RunAsync(TempDir("redmine-update-"), ["update", "--check", "--json"],
            new Dictionary<string, string?> { [Updater.ReleasesUrlEnv] = _f.ReleasesUrl });
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal(ReleasesFixture.Latest, r.Json["latest"]!.GetValue<string>());
        Assert.True(r.Json["update_available"]!.GetValue<bool>());
        Assert.Empty(_f.Mock.Hit("GET", $"/releases/download/v{ReleasesFixture.Latest}/redmine-win-x64.zip"));
    }

    [Fact]
    public async Task A_development_build_refuses_to_replace_itself()
    {
        if (Environment.GetEnvironmentVariable("REDMINE_TEST_EXE") is { Length: > 0 })
        {
            // The published (Native AOT) exe would really replace itself; this case is for the development build only.
            return;
        }
        var r = await RunAsync(TempDir("redmine-update-"), ["update"], new Dictionary<string, string?> { [Updater.ReleasesUrlEnv] = _f.ReleasesUrl });
        Assert.Equal(1, r.Code);
        Assert.Contains("開発用のビルド", r.Stderr);
        Assert.Empty(_f.Mock.Requests);
    }
}
