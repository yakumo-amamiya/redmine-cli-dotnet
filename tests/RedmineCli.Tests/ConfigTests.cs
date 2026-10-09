using System.Text.Json.Nodes;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>.redmine.json and the variable names (the Node version's test/config.test.js).</summary>
public sealed class ConfigTests
{
    private static CliException Throws(Action action) => Assert.ThrowsAny<CliException>(action);

    [Fact]
    public void Find_looks_up_the_parent_directories()
    {
        var root = TempDir("redmine-cli-config-");
        var nested = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        Assert.Null(ConfigFile.Find(nested));
        var file = ConfigFile.Write(root, "https://redmine.example.co.jp/", "demo", null, force: false);
        Assert.Equal(file, ConfigFile.Find(nested));
        // The nearer one wins.
        var nearer = ConfigFile.Write(Path.Combine(root, "a"), "https://other.example.co.jp", "other", null, force: false);
        Assert.Equal(nearer, ConfigFile.Find(nested));
    }

    [Fact]
    public void Load_drops_the_trailing_slash_and_checks_the_identifier()
    {
        var root = TempDir("redmine-cli-config-");
        ConfigFile.Write(root, "https://redmine.example.co.jp/", "demo", null, force: false);
        var config = ConfigFile.Load(root);
        Assert.Equal("https://redmine.example.co.jp", config.Url);
        Assert.Equal("demo", config.Project);
        var body = File.ReadAllBytes(config.File);
        Assert.NotEqual(0xEF, body[0]);
        var text = File.ReadAllText(config.File);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void Load_without_a_file_fails_with_3()
    {
        Assert.Equal(Exit.Config, Throws(() => ConfigFile.Load(TempDir("redmine-cli-config-"))).ExitCode);
    }

    [Fact]
    public void Invalid_contents_fail_with_3()
    {
        var root = TempDir("redmine-cli-config-");
        var file = Path.Combine(root, ".redmine.json");
        File.WriteAllText(file, """{"url": "https://x.example", "project": "123"}""");
        var e = Throws(() => ConfigFile.Load(root));
        Assert.Equal(Exit.Config, e.ExitCode);
        Assert.Contains("識別子", e.Message);
        File.WriteAllText(file, """{"url": "ftp://x.example", "project": "demo"}""");
        e = Throws(() => ConfigFile.Load(root));
        Assert.Equal(Exit.Config, e.ExitCode);
        Assert.Contains("http", e.Message);
        File.WriteAllText(file, "not json");
        Assert.Equal(Exit.Config, Throws(() => ConfigFile.Load(root)).ExitCode);
        File.WriteAllText(file, """{"url": "https://x.example", "project": "demo", "env": 5}""");
        Assert.Equal(Exit.Config, Throws(() => ConfigFile.Load(root)).ExitCode);
    }

    [Fact]
    public void Write_does_not_overwrite_without_force()
    {
        var root = TempDir("redmine-cli-config-");
        ConfigFile.Write(root, "https://a.example", "demo", null, force: false);
        Assert.Equal(Exit.Error, Throws(() => ConfigFile.Write(root, "https://b.example", "demo", null, force: false)).ExitCode);
        ConfigFile.Write(root, "https://b.example", "demo", null, force: true);
        Assert.Equal("https://b.example", ConfigFile.Load(root).Url);
    }

    [Fact]
    public void The_variable_name_is_the_identifier_upper_cased_with_underscores()
    {
        Assert.Equal("REDMINE_API_KEY_MY_PROJECT", EnvNames.ApiKey("my-project"));
        Assert.Equal("REDMINE_API_KEY_INFRA_2026", EnvNames.ApiKey("infra_2026"));
        Throws(() => EnvNames.ApiKey("Bad Name"));
    }

    [Fact]
    public void An_env_alias_names_the_variable_instead_of_the_identifier()
    {
        Assert.Equal("REDMINE_API_KEY_HOSYU", EnvNames.ApiKey("plan", "hosyu"));
        Assert.Equal("REDMINE_API_KEY_HOSYU_2026", EnvNames.ApiKey("plan", "hosyu-2026"));
        Throws(() => EnvNames.ApiKey("plan", "bad name"));
        var root = TempDir("redmine-cli-config-");
        ConfigFile.Write(root, "https://a.example", "plan", "hosyu", force: false);
        var config = ConfigFile.Load(root);
        Assert.Equal("hosyu", config.Env);
        Assert.Equal("REDMINE_API_KEY_HOSYU", EnvNames.ApiKey(config));
        AssertJson("""{ "url": "https://a.example", "project": "plan", "env": "hosyu" }""", JsonNode.Parse(File.ReadAllText(config.File)));
        var names = EnvNames.ClientCert(config);
        Assert.Equal(("REDMINE_CLIENT_CERT_HOSYU", "REDMINE_CLIENT_KEY_HOSYU", "REDMINE_CLIENT_CERT_PASSWORD_HOSYU"), (names.Cert, names.Key, names.Password));
    }

    [Fact]
    public void GetApiKey_reads_the_projects_own_variable_only()
    {
        var env = new Dictionary<string, string?> { ["REDMINE_API_KEY"] = "generic" };
        var e = Throws(() => EnvNames.GetApiKey("demo", null, name => env.GetValueOrDefault(name)));
        Assert.Equal(Exit.Config, e.ExitCode);
        Assert.Contains("REDMINE_API_KEY_DEMO", e.Message);
        env["REDMINE_API_KEY_DEMO"] = " specific ";
        Assert.Equal("specific", EnvNames.GetApiKey("demo", null, name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void NormalizeUrl_and_ValidateIdentifier()
    {
        Assert.Equal("https://x.example/redmine", ConfigFile.NormalizeUrl("https://x.example/redmine/?a=1#f"));
        Throws(() => ConfigFile.NormalizeUrl("nope"));
        Assert.Equal("my-project_1", ConfigFile.ValidateIdentifier("my-project_1"));
        // An identifier with upper case is kept as it is.
        Assert.Equal("Hosyu", ConfigFile.ValidateIdentifier("Hosyu"));
        Assert.Equal("REDMINE_API_KEY_HOSYU", EnvNames.ApiKey("Hosyu"));
        Throws(() => ConfigFile.ValidateIdentifier("My Project"));
        Throws(() => ConfigFile.ValidateIdentifier("42"));
    }

    [Fact]
    public void ParseId_takes_a_number_with_or_without_a_hash()
    {
        Assert.Equal(123, Context.ParseId("123"));
        Assert.Equal(123, Context.ParseId("#123"));
        Assert.Equal(Exit.Usage, Throws(() => Context.ParseId("12a")).ExitCode);
        Assert.Equal(Exit.Usage, Throws(() => Context.ParseId("")).ExitCode);
    }
}

/// <summary>Table layout and the small formatters.</summary>
public sealed class OutputTests
{
    [Fact]
    public void Full_width_characters_count_as_two()
    {
        Assert.Equal(4, Output.DisplayWidth("aあc"));
        Assert.Equal(6, Output.DisplayWidth("日本語"));
    }

    [Fact]
    public void Truncate_cuts_by_display_width_and_flattens_whitespace()
    {
        Assert.Equal("a b c", Output.Truncate("a\n b\tc", 10));
        Assert.Equal("日本…", Output.Truncate("日本語のテキスト", 6));
    }

    [Fact]
    public void Table_aligns_by_display_width()
    {
        var table = Output.Table(["日本", "abc"], new Column<string>("名前", s => s), new Column<string>("n", s => s.Length.ToString(), Right: true));
        Assert.Equal("名前  n\n----  -\n日本  2\nabc   3", table);
    }

    [Fact]
    public void ShortDate_and_FormatSize()
    {
        Assert.Equal("2026-09-01 09:30", Output.ShortDate("2026-09-01T09:30:15Z"));
        Assert.Equal("2026-09-01 09:30", Output.ShortDate("2026-09-01T09:30:15+09:00"));
        Assert.Equal("", Output.ShortDate((string?)null));
        Assert.Equal("5 B", Output.FormatSize(5));
        Assert.Equal("1.5 KB", Output.FormatSize(1536));
    }
}
