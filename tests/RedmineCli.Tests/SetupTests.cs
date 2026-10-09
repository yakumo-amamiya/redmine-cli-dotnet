using RedmineCli.Commands;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>
/// redmine setup's dialogue, run in the test process with a scripted console and a fake user environment (the real user
/// variables are never touched).
/// </summary>
public sealed class SetupTests
{
    private const string KeyVar = "REDMINE_API_KEY_DEMO";
    private const string CertVar = "REDMINE_CLIENT_CERT_DEMO";
    private const string KeyFileVar = "REDMINE_CLIENT_KEY_DEMO";
    private const string PasswordVar = "REDMINE_CLIENT_CERT_PASSWORD_DEMO";

    private static readonly RedmineConfig Config = new(@"C:\repo\.redmine.json", "https://redmine.example.co.jp", "demo", null);

    private static string Fixture(string name) => TlsFixture.Fixture(name);

    private sealed class ScriptedConsole(IEnumerable<string?> lines, IEnumerable<string?> secrets) : ISetupConsole
    {
        private readonly Queue<string?> _lines = new(lines);
        private readonly Queue<string?> _secrets = new(secrets);

        public List<string> Written { get; } = [];

        public string All => string.Join("\n", Written);

        public string? ReadLine(string prompt)
        {
            Written.Add(prompt);
            Assert.True(_lines.Count > 0, $"setup asked for a line nobody scripted: {prompt}\n{All}");
            return _lines.Dequeue();
        }

        public string? ReadSecret(string prompt)
        {
            Written.Add(prompt);
            Assert.True(_secrets.Count > 0, $"setup asked for a secret nobody scripted: {prompt}\n{All}");
            return _secrets.Dequeue();
        }

        public void Write(string line) => Written.Add(line);

        public void AssertAllUsed()
        {
            Assert.True(_lines.Count == 0, $"unused lines: {string.Join(", ", _lines)}\n{All}");
            Assert.True(_secrets.Count == 0, $"unused secrets\n{All}");
        }
    }

    private sealed class FakeEnvironment(Dictionary<string, string>? initial = null) : IUserEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(initial ?? [], StringComparer.OrdinalIgnoreCase);

        public int Writes { get; private set; }

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string? value)
        {
            Writes++;
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = value;
            }
        }
    }

    private static readonly Globals Plain = new(Json: false, Yes: false, DryRun: false, Verbose: false);

    private static async Task<(int Code, Func<string, string?>? Checked)> Run(ScriptedConsole io, FakeEnvironment env, Globals? g = null)
    {
        Func<string, string?>? seen = null;
        var code = await new SetupFlow(Config, io, env).RunAsync(g ?? Plain, getEnv =>
        {
            seen = getEnv;
            return Task.FromResult(true);
        });
        io.AssertAllUsed();
        return (code, seen);
    }

    [Fact]
    public async Task A_new_key_is_written_and_never_shown()
    {
        var io = new ScriptedConsole(lines: ["n", "y"], secrets: ["  0123456789abcdef  "]);
        var env = new FakeEnvironment();
        var (code, check) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal("0123456789abcdef", env.Values[KeyVar]);
        Assert.Single(env.Values);
        Assert.DoesNotContain("0123456789abcdef", io.All);
        Assert.Contains("https://redmine.example.co.jp/my/account", io.All);
        Assert.Contains("(入力した値。表示しません)", io.All);
        // The connection check sees the value about to be used.
        Assert.Equal("0123456789abcdef", check!(KeyVar));
    }

    [Fact]
    public async Task An_empty_answer_keeps_the_existing_key_and_writes_nothing()
    {
        var io = new ScriptedConsole(lines: ["n"], secrets: [""]);
        var env = new FakeEnvironment(new() { [KeyVar] = "old" });
        var (code, check) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal(0, env.Writes);
        Assert.Contains("変更はありません", io.All);
        Assert.Equal("old", check!(KeyVar));
    }

    [Fact]
    public async Task A_PFX_password_is_checked_and_asked_again_when_wrong()
    {
        var pfx = Fixture("client.pfx");
        var io = new ScriptedConsole(lines: ["y", pfx, "y"], secrets: ["key", "wrong", "testpass"]);
        var env = new FakeEnvironment();
        var (code, _) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal(pfx, env.Values[CertVar]);
        Assert.Equal("testpass", env.Values[PasswordVar]);
        Assert.False(env.Values.ContainsKey(KeyFileVar));
        Assert.Contains("パスフレーズが合いません", io.All);
        Assert.Contains("読めました: subject=CN=redmine-cli test client", io.All);
        Assert.DoesNotContain("testpass", io.All);
    }

    [Fact]
    public async Task A_PEM_with_an_encrypted_key_takes_a_quoted_path_the_key_file_and_the_passphrase()
    {
        var io = new ScriptedConsole(lines: ["y", $"\"{Fixture("client.pem")}\"", Fixture("client-enc.key"), "y"], secrets: ["key", "keypass"]);
        var env = new FakeEnvironment();
        var (code, _) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal(Fixture("client.pem"), env.Values[CertVar]);
        Assert.Equal(Fixture("client-enc.key"), env.Values[KeyFileVar]);
        Assert.Equal("keypass", env.Values[PasswordVar]);
    }

    [Fact]
    public async Task A_missing_file_is_asked_again()
    {
        var missing = Path.Combine(TempDir("redmine-cli-setup-"), "missing.pfx");
        var io = new ScriptedConsole(lines: ["y", missing, Fixture("client-nopass.pfx"), "y"], secrets: ["key", ""]);
        var env = new FakeEnvironment();
        var (code, _) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Contains($"ファイルがありません: {missing}", io.All);
        Assert.Equal(Fixture("client-nopass.pfx"), env.Values[CertVar]);
        Assert.False(env.Values.ContainsKey(PasswordVar));
    }

    [Fact]
    public async Task Switching_from_PEM_to_PFX_deletes_the_key_file_variable()
    {
        var io = new ScriptedConsole(lines: ["y", Fixture("client.pfx"), "y"], secrets: ["", "testpass"]);
        var env = new FakeEnvironment(new()
        {
            [KeyVar] = "k",
            [CertVar] = Fixture("client.pem"),
            [KeyFileVar] = Fixture("client.key"),
        });
        var (code, check) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal(Fixture("client.pfx"), env.Values[CertVar]);
        Assert.False(env.Values.ContainsKey(KeyFileVar));
        Assert.Contains("(消す)", io.All);
        Assert.Null(check!(KeyFileVar));
    }

    [Fact]
    public async Task D_deletes_the_certificate_settings()
    {
        var io = new ScriptedConsole(lines: ["d", "y"], secrets: [""]);
        var env = new FakeEnvironment(new() { [KeyVar] = "k", [CertVar] = Fixture("client.pfx"), [PasswordVar] = "testpass" });
        var (code, _) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Equal([KeyVar], env.Values.Keys);
    }

    [Fact]
    public async Task Three_wrong_passwords_give_up_on_the_certificate_only()
    {
        var io = new ScriptedConsole(lines: ["y", Fixture("client.pfx"), "y"], secrets: ["key", "a", "b", "c"]);
        var env = new FakeEnvironment();
        var (code, _) = await Run(io, env);
        Assert.Equal(0, code);
        Assert.Contains("3 回合わなかった", io.All);
        Assert.Equal([KeyVar], env.Values.Keys);
    }

    [Fact]
    public async Task Declining_writes_nothing_and_exits_4()
    {
        var io = new ScriptedConsole(lines: ["n", "n"], secrets: ["key"]);
        var env = new FakeEnvironment();
        var e = await Assert.ThrowsAnyAsync<CliException>(() => new SetupFlow(Config, io, env).RunAsync(Plain, _ => Task.FromResult(true)));
        Assert.Equal(Exit.Refused, e.ExitCode);
        Assert.Equal(0, env.Writes);
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_does_not_connect()
    {
        var io = new ScriptedConsole(lines: ["n"], secrets: ["key"]);
        var env = new FakeEnvironment();
        var (code, check) = await Run(io, env, Plain with { DryRun = true });
        Assert.Equal(0, code);
        Assert.Equal(0, env.Writes);
        Assert.Null(check);
        Assert.Contains("--dry-run のため書きません", io.All);
    }

    [Fact]
    public async Task Yes_skips_the_final_question()
    {
        var io = new ScriptedConsole(lines: ["n"], secrets: ["key"]);
        var env = new FakeEnvironment();
        var (code, _) = await Run(io, env, Plain with { Yes = true });
        Assert.Equal(0, code);
        Assert.Equal("key", env.Values[KeyVar]);
    }

    [Fact]
    public async Task Without_a_terminal_setup_refuses_with_4_and_shows_how_to_set_the_variables_by_hand()
    {
        var dir = TempDir("redmine-cli-setup-");
        WriteConfig(dir, "https://redmine.example.co.jp", "demo");
        var r = await RunAsync(dir, "setup");
        Assert.True(r.Code == 4, r.ToString());
        Assert.Contains("端末", r.Stderr);
        Assert.Contains("setx REDMINE_API_KEY_DEMO", r.Stderr);
        Assert.Contains("setx REDMINE_CLIENT_CERT_DEMO", r.Stderr);
        Assert.Contains("https://redmine.example.co.jp/my/account", r.Stderr);
    }

    [Fact]
    public async Task A_missing_key_points_at_setup()
    {
        var dir = TempDir("redmine-cli-setup-");
        WriteConfig(dir, "https://redmine.example.co.jp", "demo");
        var r = await RunAsync(dir, ["me"], new Dictionary<string, string?> { [KeyVar] = null });
        Assert.Equal(3, r.Code);
        Assert.Contains("redmine setup", r.Stderr);
        Assert.Contains("https://redmine.example.co.jp/my/account", r.Stderr);
    }
}
