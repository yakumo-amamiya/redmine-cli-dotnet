using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedmineCli.Tests;

public sealed record CliResult(int Code, string Stdout, string Stderr)
{
    public JsonNode Json => JsonNode.Parse(Stdout) ?? throw new InvalidOperationException("stdout is JSON null");

    public override string ToString() => $"exit {Code}\n--- stdout\n{Stdout}\n--- stderr\n{Stderr}";
}

/// <summary>
/// Runs redmine.exe as a child process. Its stdin is a pipe, so it is not interactive. The proxy and REDMINE_* variables of
/// the machine are removed; REDMINE_API_KEY_DEMO=test-key is set, as the Node version's tests did.
/// </summary>
public static partial class CliRunner
{
    // REDMINE_TEST_EXE runs the same tests against another build, such as the published Native AOT exe (CI does).
    private static readonly string Exe = Environment.GetEnvironmentVariable("REDMINE_TEST_EXE") is { Length: > 0 } exe
        ? Path.GetFullPath(exe)
        : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "redmine.exe" : "redmine");

    [GeneratedRegex("^(https?_proxy|all_proxy|no_proxy|redmine_.*|node_extra_ca_certs)$", RegexOptions.IgnoreCase)]
    private static partial Regex Scrubbed();

    public static async Task<CliResult> RunAsync(string cwd, IEnumerable<string> args, IReadOnlyDictionary<string, string?>? env = null, string? input = null)
    {
        var start = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (Scrubbed().IsMatch(key))
            {
                start.Environment.Remove(key);
            }
        }
        start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        start.Environment["REDMINE_API_KEY_DEMO"] = "test-key";
        if (!start.Environment.ContainsKey("DOTNET_ROOT"))
        {
            // The exe is framework-dependent in tests: point it at the runtime running the tests (a user-local SDK included).
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }
        foreach (var (key, value) in env ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(key);
            }
            else
            {
                start.Environment[key] = value;
            }
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {Exe}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input ?? "");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"redmine {string.Join(" ", args)} did not finish in 60 seconds");
        }
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    public static Task<CliResult> RunAsync(string cwd, params string[] args) => RunAsync(cwd, args, null);

    /// <summary>A new empty directory under the temp folder.</summary>
    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes .redmine.json in dir (LF, a final newline).</summary>
    public static void WriteConfig(string dir, string url, string project, string? env = null)
    {
        var config = new JsonObject { ["url"] = url, ["project"] = project };
        if (env is not null)
        {
            config["env"] = env;
        }
        File.WriteAllText(Path.Combine(dir, ".redmine.json"), config.ToJsonString() + "\n");
    }

    public static void AssertJson(string expected, JsonNode? actual)
    {
        var want = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(want, actual), $"expected {want?.ToJsonString()}\n  actual {actual?.ToJsonString()}");
    }
}
