using System.Text.Json.Nodes;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>A mock Redmine (plain HTTP) and a working directory whose .redmine.json points at it (project "demo").</summary>
public sealed class RedmineFixture : IAsyncLifetime
{
    public const string IssueInside = """
        {
          "id": 1, "subject": "inside issue", "project": { "id": 1, "name": "Demo" },
          "tracker": { "id": 1, "name": "Bug" }, "status": { "id": 1, "name": "New" }, "priority": { "id": 2, "name": "Normal" },
          "author": { "id": 7, "name": "Test User" }, "done_ratio": 0,
          "created_on": "2026-09-01T00:00:00Z", "updated_on": "2026-09-02T00:00:00Z",
          "attachments": [
            { "id": 5, "filename": "a.txt", "filesize": 5, "content_type": "text/plain", "author": { "name": "Test User" }, "created_on": "2026-09-01T00:00:00Z" }
          ]
        }
        """;

    public MockRedmine Mock { get; private set; } = null!;

    public string Base => Mock.BaseUrl;

    public string Workdir { get; } = TempDir("redmine-cli-work-");

    public static JsonObject Issue(Action<JsonObject>? change = null)
    {
        var issue = JsonNode.Parse(IssueInside)!.AsObject();
        change?.Invoke(issue);
        return issue;
    }

    public static JsonObject IssueOutside() => Issue(i =>
    {
        i["id"] = 99;
        i["subject"] = "outside issue";
        i["project"] = new JsonObject { ["id"] = 2, ["name"] = "Other" };
        i["attachments"] = new JsonArray();
    });

    public async Task InitializeAsync()
    {
        Mock = await MockRedmine.StartAsync();
        WriteConfig(Workdir, Base, "demo");
        Reset();
    }

    public Task DisposeAsync() => Mock.DisposeAsync().AsTask();

    /// <summary>The routes every test starts from, and no recorded requests.</summary>
    public void Reset()
    {
        var m = Mock;
        m.Routes.Clear();
        m.ClearRequests();
        m.Route("GET /projects/demo.json", """{ "project": { "id": 1, "name": "Demo", "identifier": "demo" } }""");
        m.Route("GET /users/current.json", """{ "user": { "id": 7, "login": "tester", "firstname": "Test", "lastname": "User" } }""");
        m.Route("GET /issues/1.json", _ => new MockResponse(Json: new JsonObject { ["issue"] = Issue() }));
        m.Route("GET /issues/99.json", _ => new MockResponse(Json: new JsonObject { ["issue"] = IssueOutside() }));
        m.Route("PUT /issues/1.json", _ => new MockResponse(204));
        m.Route("POST /issues.json", r => new MockResponse(201, new JsonObject
        {
            ["issue"] = Issue(i =>
            {
                i["id"] = 100;
                i["subject"] = r.Json?["issue"]?["subject"]?.DeepClone();
            }),
        }));
        m.Route("POST /uploads.json", """{ "upload": { "id": 9, "token": "tok123" } }""", 201);
        m.Route("GET /attachments/download/5/a.txt", _ => new MockResponse(Body: "hello"));
        m.Route("GET /issues.json", _ => new MockResponse(Json: new JsonObject
        {
            ["issues"] = new JsonArray(Issue()),
            ["total_count"] = 1,
            ["offset"] = 0,
            ["limit"] = 25,
        }));
        m.Route("GET /projects/1.json", """
            { "project": { "id": 1, "name": "Demo", "identifier": "demo",
                           "issue_custom_fields": [ { "id": 11, "name": "対象OS" }, { "id": 12, "name": "確認済み" }, { "id": 13, "name": "顧客" } ] } }
            """);
        m.Route("GET /custom_fields.json", """
            { "custom_fields": [
                { "id": 11, "name": "対象OS", "customized_type": "issue", "field_format": "list", "multiple": true, "is_required": false,
                  "possible_values": [ { "value": "Windows" }, { "value": "Linux" } ], "trackers": [ { "id": 1, "name": "Bug" } ] },
                { "id": 12, "name": "確認済み", "customized_type": "issue", "field_format": "bool", "multiple": false, "is_required": false, "trackers": [] },
                { "id": 13, "name": "顧客", "customized_type": "issue", "field_format": "string", "multiple": false, "is_required": true, "trackers": [] },
                { "id": 14, "name": "作業場所", "customized_type": "time_entry", "field_format": "string", "multiple": false }
            ] }
            """);
        m.Route("GET /issue_statuses.json", """
            { "issue_statuses": [ { "id": 1, "name": "New" }, { "id": 2, "name": "In Progress" }, { "id": 5, "name": "Closed", "is_closed": true } ] }
            """);
        m.Route("GET /enumerations/time_entry_activities.json", """
            { "time_entry_activities": [ { "id": 8, "name": "Design" }, { "id": 9, "name": "Development", "is_default": true } ] }
            """);
        m.Route("POST /time_entries.json", r =>
        {
            var entry = r.Json!["time_entry"]!.DeepClone().AsObject();
            entry["id"] = 50;
            entry["activity"] = new JsonObject { ["id"] = 9, ["name"] = "Development" };
            entry["spent_on"] = "2026-09-08";
            return new MockResponse(201, new JsonObject { ["time_entry"] = entry });
        });
    }
}

/// <summary>The CLI against the mock Redmine: the safety checks, the requests sent, the exit codes (the Node version's test/cli.test.js).</summary>
public sealed class CliTests : IClassFixture<RedmineFixture>
{
    private readonly RedmineFixture _f;

    public CliTests(RedmineFixture fixture)
    {
        _f = fixture;
        _f.Reset();
    }

    private MockRedmine Mock => _f.Mock;

    private Task<CliResult> Run(params string[] args) => RunAsync(_f.Workdir, args);

    private Task<CliResult> Run(IReadOnlyDictionary<string, string?> env, params string[] args) => RunAsync(_f.Workdir, args, env);

    [Fact]
    public async Task No_arguments_shows_the_help_and_exits_0()
    {
        var r = await Run();
        Assert.Equal(0, r.Code);
        Assert.Contains("redmine guide", r.Stdout);
    }

    [Fact]
    public async Task Target_json_shows_the_local_settings_and_the_variable_to_read_without_network()
    {
        var r = await Run("target", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var json = r.Json;
        Assert.Equal("demo", json["project"]!.GetValue<string>());
        Assert.Equal(_f.Base, json["url"]!.GetValue<string>());
        Assert.Equal("REDMINE_API_KEY_DEMO", json["api_key_env"]!.GetValue<string>());
        Assert.True(json["api_key_set"]!.GetValue<bool>());
        Assert.Empty(Mock.Requests);
    }

    [Fact]
    public async Task Without_redmine_json_it_exits_3()
    {
        var r = await RunAsync(TempDir("redmine-cli-empty-"), "target");
        Assert.Equal(3, r.Code);
        Assert.Contains("redmine init", r.Stderr);
    }

    [Fact]
    public async Task Without_the_project_variable_it_exits_3_and_names_it()
    {
        var r = await Run(new Dictionary<string, string?> { ["REDMINE_API_KEY_DEMO"] = null }, "me");
        Assert.Equal(3, r.Code);
        Assert.Contains("REDMINE_API_KEY_DEMO", r.Stderr);
    }

    [Fact]
    public async Task The_generic_REDMINE_API_KEY_alone_does_not_work()
    {
        var r = await Run(new Dictionary<string, string?> { ["REDMINE_API_KEY_DEMO"] = null, ["REDMINE_API_KEY"] = "generic-key" }, "me");
        Assert.Equal(3, r.Code);
        Assert.Empty(Mock.Requests);
        Assert.DoesNotContain("generic-key", r.Stdout + r.Stderr);
    }

    [Fact]
    public async Task A_rewritten_project_finds_no_variable_and_stops()
    {
        var other = TempDir("redmine-cli-other-");
        WriteConfig(other, _f.Base, "other-proj");
        var r = await RunAsync(other, "issues", "create", "--subject", "x", "--yes");
        Assert.Equal(3, r.Code);
        Assert.Contains("REDMINE_API_KEY_OTHER_PROJ", r.Stderr);
        Assert.Empty(Mock.Requests);
    }

    [Fact]
    public async Task An_env_alias_in_redmine_json_names_the_variable()
    {
        var dir = TempDir("redmine-cli-alias-");
        WriteConfig(dir, _f.Base, "demo", env: "hosyu");
        var env = new Dictionary<string, string?> { ["REDMINE_API_KEY_DEMO"] = null, ["REDMINE_API_KEY_HOSYU"] = "alias-key" };
        var target = await RunAsync(dir, ["target", "--json"], env);
        Assert.True(target.Code == 0, target.ToString());
        Assert.Equal("REDMINE_API_KEY_HOSYU", target.Json["api_key_env"]!.GetValue<string>());
        var r = await RunAsync(dir, ["me", "--json"], env);
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("alias-key", Mock.Hit("GET", "/users/current.json")[0].Headers["x-redmine-api-key"]);
        var missing = await RunAsync(dir, ["me"], new Dictionary<string, string?> { ["REDMINE_API_KEY_HOSYU"] = null });
        Assert.Equal(3, missing.Code);
        Assert.Contains("REDMINE_API_KEY_HOSYU", missing.Stderr);
    }

    [Fact]
    public async Task Init_env_writes_the_alias()
    {
        var dir = TempDir("redmine-cli-initenv-");
        var r = await RunAsync(dir, ["init", "--url", _f.Base, "--project", "demo", "--env", "hosyu", "--json"],
            new Dictionary<string, string?> { ["REDMINE_API_KEY_DEMO"] = null, ["REDMINE_API_KEY_HOSYU"] = "alias-key" });
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("REDMINE_API_KEY_HOSYU", r.Json["api_key_env"]!.GetValue<string>());
        var written = File.ReadAllText(Path.Combine(dir, ".redmine.json"));
        Assert.Equal("hosyu", JsonNode.Parse(written)!["env"]!.GetValue<string>());
        Assert.DoesNotContain("\r", written);
        Assert.EndsWith("\n", written);
    }

    [Fact]
    public async Task Me_json_returns_the_user_and_the_target_and_sends_the_key_in_a_header()
    {
        var r = await Run("me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("tester", r.Json["user"]!["login"]!.GetValue<string>());
        Assert.Equal("demo", r.Json["target"]!["project"]!["identifier"]!.GetValue<string>());
        Assert.Equal("test-key", Mock.Hit("GET", "/users/current.json")[0].Headers["x-redmine-api-key"]);
        Assert.DoesNotContain("test-key", r.Stdout + r.Stderr);
    }

    [Fact]
    public async Task Issues_list_filters_by_the_target_project_and_json_is_the_response_as_is()
    {
        var r = await Run("issues", "list", "--mine", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var q = Mock.Hit("GET", "/issues.json")[0].Query;
        Assert.Equal("demo", q["project_id"]);
        Assert.Equal("open", q["status_id"]);
        Assert.Equal("me", q["assigned_to_id"]);
        Assert.Equal(1, r.Json["total_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Issues_list_prints_a_table()
    {
        var r = await Run("issues", "ls");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("#1", r.Stdout);
        Assert.Contains("inside issue", r.Stdout);
        Assert.Contains("(1 件表示 / 該当 1 件)", r.Stdout);
    }

    [Fact]
    public async Task Issues_show_reads_an_issue_outside_with_in_target_false_and_a_warning()
    {
        var r = await Run("issues", "show", "99", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.False(r.Json["in_target"]!.GetValue<bool>());
        Assert.Contains("対象プロジェクト外", r.Stderr);
    }

    [Fact]
    public async Task Issues_show_prints_the_issue()
    {
        var r = await Run("issues", "show", "#1");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("#1 inside issue", r.Stdout);
        Assert.Contains($"{_f.Base}/issues/1", r.Stdout);
        Assert.Contains("a.txt", r.Stdout);
    }

    [Fact]
    public async Task Update_of_an_issue_outside_exits_4_and_sends_no_PUT()
    {
        var r = await Run("issues", "update", "99", "--note", "x", "--yes");
        Assert.True(r.Code == 4, r.ToString());
        Assert.Contains("対象プロジェクト「Demo」", r.Stderr);
        Assert.Empty(Mock.Hit("PUT", "/issues/99.json"));
    }

    [Fact]
    public async Task Without_yes_and_without_a_terminal_it_exits_4_and_sends_nothing()
    {
        var r = await Run("issues", "update", "1", "--note", "x");
        Assert.True(r.Code == 4, r.ToString());
        Assert.Contains("--yes", r.Stderr);
        Assert.Empty(Mock.Hit("PUT", "/issues/1.json"));
    }

    [Fact]
    public async Task Dry_run_sends_nothing_and_prints_the_request()
    {
        var r = await Run("issues", "create", "--subject", "hello", "--status", "in prog", "--dry-run");
        Assert.True(r.Code == 0, r.ToString());
        var json = r.Json;
        Assert.True(json["dry_run"]!.GetValue<bool>());
        Assert.Equal("POST", json["request"]!["method"]!.GetValue<string>());
        Assert.Equal(1, json["request"]!["body"]!["issue"]!["project_id"]!.GetValue<int>());
        Assert.Equal(2, json["request"]!["body"]!["issue"]!["status_id"]!.GetValue<int>());
        Assert.Empty(Mock.Hit("POST", "/issues.json"));
    }

    [Fact]
    public async Task Issues_create_yes_posts_with_the_target_project_id()
    {
        var r = await Run("issues", "create", "--subject", "hello", "--yes", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var post = Mock.Hit("POST", "/issues.json")[0];
        Assert.Equal(1, post.Json!["issue"]!["project_id"]!.GetValue<int>());
        Assert.Equal("application/json", post.Headers["content-type"]);
        Assert.Equal(100, r.Json["issue"]!["id"]!.GetValue<int>());
        Assert.Contains("プロジェクト : Demo (demo)", r.Stderr);
    }

    [Fact]
    public async Task Issues_update_yes_checks_the_project_then_puts_and_json_returns_the_issue_after()
    {
        var r = await Run("issues", "update", "1", "--status", "closed", "--note", "done", "--yes", "--json");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "issue": { "status_id": 5, "notes": "done" } }""", Mock.Hit("PUT", "/issues/1.json")[0].Json);
        Assert.Equal(1, r.Json["issue"]!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_unresolved_name_exits_2_with_the_choices()
    {
        var r = await Run("issues", "update", "1", "--status", "nonexistent", "--yes");
        Assert.True(r.Code == 2, r.ToString());
        Assert.Contains("In Progress", r.Stderr);
    }

    [Fact]
    public async Task Issues_comment_sends_the_same_PUT_as_update_note()
    {
        var r = await Run("issues", "comment", "1", "looks good", "--yes");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "issue": { "notes": "looks good" } }""", Mock.Hit("PUT", "/issues/1.json")[0].Json);
    }

    [Fact]
    public async Task A_comment_starting_with_an_at_sign_is_text_not_a_response_file()
    {
        var r = await Run("issues", "comment", "1", "@someone 確認お願いします", "--yes");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "issue": { "notes": "@someone 確認お願いします" } }""", Mock.Hit("PUT", "/issues/1.json")[0].Json);
    }

    [Fact]
    public async Task A_comment_from_stdin_is_read_as_UTF8()
    {
        var r = await RunAsync(_f.Workdir, ["issues", "comment", "1", "--file", "-", "--yes"], input: "日本語の本文\n2 行目\n");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "issue": { "notes": "日本語の本文\n2 行目\n" } }""", Mock.Hit("PUT", "/issues/1.json")[0].Json);
    }

    [Fact]
    public async Task Issues_attach_uploads_then_puts_the_token()
    {
        var file = Path.Combine(_f.Workdir, "upload.txt");
        File.WriteAllText(file, "payload");
        var r = await Run("issues", "attach", "1", file, "--note", "attached", "--yes");
        Assert.True(r.Code == 0, r.ToString());
        var up = Mock.Hit("POST", "/uploads.json")[0];
        Assert.Equal("upload.txt", up.Query["filename"]);
        Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(up.Raw));
        Assert.Equal("7", up.Headers["content-length"]);
        var put = Mock.Hit("PUT", "/issues/1.json")[0];
        Assert.Equal("tok123", put.Json!["issue"]!["uploads"]![0]!["token"]!.GetValue<string>());
        Assert.Equal("attached", put.Json!["issue"]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task Issues_download_saves_through_the_issue_and_refuses_an_existing_file_without_force()
    {
        var dir = Path.Combine(_f.Workdir, "dl");
        var r = await Run("issues", "download", "1", "--dir", dir, "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("a.txt", r.Json["downloaded"]![0]!["filename"]!.GetValue<string>());
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dir, "a.txt")));
        var again = await Run("issues", "download", "1", "--dir", dir);
        Assert.Equal(1, again.Code);
        Assert.Contains("--force", again.Stderr);
        var forced = await Run("issues", "download", "1", "--dir", dir, "--force");
        Assert.True(forced.Code == 0, forced.ToString());
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    [Fact]
    public async Task Time_log_posts_with_the_default_activity()
    {
        var r = await Run("time", "log", "1", "--hours", "1.5", "--comment", "work", "--yes", "--json");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "time_entry": { "issue_id": 1, "hours": 1.5, "activity_id": 9, "comments": "work" } }""",
            Mock.Hit("POST", "/time_entries.json")[0].Json);
    }

    [Fact]
    public async Task Time_log_refuses_an_issue_outside()
    {
        var r = await Run("time", "log", "99", "--hours", "1", "--yes");
        Assert.Equal(4, r.Code);
        Assert.Empty(Mock.Hit("POST", "/time_entries.json"));
    }

    [Fact]
    public async Task Api_GET_is_free_and_POST_needs_unsafe()
    {
        var get = await Run("api", "GET", "/issues.json?limit=1");
        Assert.True(get.Code == 0, get.ToString());
        Assert.Equal("1", Mock.Hit("GET", "/issues.json")[0].Query["limit"]);
        var post = await Run("api", "POST", "/issues.json", "--data", """{"issue":{}}""", "--yes");
        Assert.Equal(4, post.Code);
        Assert.Empty(Mock.Hit("POST", "/issues.json"));
        var unsafeRun = await Run("api", "POST", "/issues.json", "--data", """{"issue":{"subject":"raw"}}""", "--unsafe", "--yes");
        Assert.True(unsafeRun.Code == 0, unsafeRun.ToString());
        Assert.Equal("raw", Mock.Hit("POST", "/issues.json")[0].Json!["issue"]!["subject"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_422_from_the_server_exits_5_with_the_reason()
    {
        Mock.Route("POST /issues.json", """{ "errors": ["Subject cannot be blank"] }""", 422);
        var r = await Run("issues", "create", "--subject", "x", "--yes");
        Assert.Equal(5, r.Code);
        Assert.Contains("Subject cannot be blank", r.Stderr);
    }

    [Fact]
    public async Task A_non_JSON_answer_after_an_SSO_redirect_exits_5_with_the_final_url()
    {
        Mock.Route("GET /projects/demo.json", _ => new MockResponse(302, Headers: new Dictionary<string, string> { ["location"] = "/login" }));
        Mock.Route("GET /login", _ => new MockResponse(Body: "<html><head><title>Corporate SSO</title></head><body>login</body></html>",
            Headers: new Dictionary<string, string> { ["content-type"] = "text/html" }));
        var r = await Run("me");
        Assert.True(r.Code == 5, r.ToString());
        Assert.Matches(@"リダイレクト後の URL: .*/login", r.Stderr);
        Assert.Contains("Corporate SSO", r.Stderr);
        Assert.Contains("projects.json", r.Stderr);
    }

    [Fact]
    public async Task The_key_is_not_sent_to_another_origin_after_a_redirect()
    {
        // localhost and 127.0.0.1 are the same server but different origins.
        Mock.Route("GET /projects/demo.json", _ => new MockResponse(302,
            Headers: new Dictionary<string, string> { ["location"] = $"http://localhost:{Mock.Port}/login" }));
        Mock.Route("GET /login", _ => new MockResponse(Body: "<html><title>SSO</title></html>",
            Headers: new Dictionary<string, string> { ["content-type"] = "text/html" }));
        var r = await Run("me");
        Assert.Equal(5, r.Code);
        var login = Mock.Hit("GET", "/login");
        Assert.Single(login);
        Assert.False(login[0].Headers.ContainsKey("x-redmine-api-key"));
        Assert.Equal("test-key", Mock.Hit("GET", "/projects/demo.json")[0].Headers["x-redmine-api-key"]);
    }

    [Fact]
    public async Task A_url_with_a_sub_path_builds_the_API_paths_under_it()
    {
        var sub = TempDir("redmine-cli-sub-");
        WriteConfig(sub, $"{_f.Base}/adj/", "demo");
        Mock.Route("GET /adj/projects/demo.json", """{ "project": { "id": 1, "name": "Demo", "identifier": "demo" } }""");
        Mock.Route("GET /adj/users/current.json", """{ "user": { "id": 7, "login": "tester", "firstname": "T", "lastname": "U" } }""");
        var r = await RunAsync(sub, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Single(Mock.Hit("GET", "/adj/projects/demo.json"));
        Assert.Equal($"{_f.Base}/adj", r.Json["target"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Init_suggests_identifiers_when_the_one_given_does_not_exist()
    {
        var dir = TempDir("redmine-cli-init-");
        Mock.Route("GET /projects.json", """
            { "projects": [ { "id": 1, "name": "Demo", "identifier": "demo" }, { "id": 3, "name": "保守", "identifier": "hosyu-kanri" } ],
              "total_count": 2, "offset": 0, "limit": 100 }
            """);
        var r = await RunAsync(dir, ["init", "--url", _f.Base, "--project", "hosyu"], new Dictionary<string, string?> { ["REDMINE_API_KEY_HOSYU"] = "k" });
        Assert.True(r.Code == 3, r.ToString());
        Assert.Contains("hosyu-kanri (保守)", r.Stderr);
        Assert.Contains("REDMINE_API_KEY_HOSYU_KANRI", r.Stderr);
        Assert.False(File.Exists(Path.Combine(dir, ".redmine.json")));
    }

    [Fact]
    public async Task Fields_returns_the_projects_custom_fields_with_formats_and_choices()
    {
        var r = await Run("fields", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var fields = r.Json["fields"]!.AsArray();
        Assert.Equal(new[] { 11, 12, 13 }, fields.Select(f => f!["id"]!.GetValue<int>()));
        var os = fields.First(f => f!["id"]!.GetValue<int>() == 11)!;
        Assert.Equal("list", os["format"]!.GetValue<string>());
        Assert.True(os["multiple"]!.GetValue<bool>());
        Assert.Equal(new[] { "Windows", "Linux" }, os["possible_values"]!.AsArray().Select(o => o!["value"]!.GetValue<string>()));
        Assert.True(fields.First(f => f!["id"]!.GetValue<int>() == 13)!["required"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Fields_prints_a_table()
    {
        var r = await Run("fields");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("対象OS", r.Stdout);
        Assert.Contains("Windows | Linux", r.Stdout);
    }

    [Fact]
    public async Task Field_turns_names_into_ids_and_values_into_the_format()
    {
        var r = await Run("issues", "create", "--subject", "x", "--field", "対象OS=win", "--field", "対象os=Linux", "--field", "確認済み=yes",
            "--field", "顧客=ACME", "--dry-run");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""
            [ { "id": 11, "value": ["Windows", "Linux"] }, { "id": 12, "value": "1" }, { "id": 13, "value": "ACME" } ]
            """, r.Json["request"]!["body"]!["issue"]!["custom_fields"]);
        Assert.Contains("カスタム「対象OS」(id 11): [Windows, Linux]", r.Stderr);
    }

    [Fact]
    public async Task A_list_value_not_among_the_choices_exits_2_with_them()
    {
        var r = await Run("issues", "create", "--subject", "x", "--field", "対象OS=macOS", "--dry-run");
        Assert.True(r.Code == 2, r.ToString());
        Assert.Contains("Windows, Linux", r.Stderr);
    }

    [Fact]
    public async Task An_unknown_field_name_exits_2_with_the_fields()
    {
        var r = await Run("issues", "update", "1", "--field", "存在しない=1", "--yes");
        Assert.True(r.Code == 2, r.ToString());
        Assert.Contains("顧客 (13)", r.Stderr);
    }

    [Fact]
    public async Task Update_field_with_an_empty_value_clears_it()
    {
        var r = await Run("issues", "update", "1", "--field", "顧客=", "--yes");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "issue": { "custom_fields": [ { "id": 13, "value": "" } ] } }""", Mock.Hit("PUT", "/issues/1.json")[0].Json);
    }

    [Fact]
    public async Task List_field_filters_with_cf_id()
    {
        var r = await Run("issues", "list", "--field", "顧客=ACME", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("ACME", Mock.Hit("GET", "/issues.json")[0].Query["cf_13"]);
    }

    [Fact]
    public async Task Without_admin_rights_field_works_by_name_and_fields_shows_observed_values()
    {
        Mock.Route("GET /custom_fields.json", "{}", 403);
        Mock.Route("GET /issues.json", _ => new MockResponse(Json: new JsonObject
        {
            ["issues"] = new JsonArray(
                RedmineFixture.Issue(i => i["custom_fields"] = JsonNode.Parse("""[ { "id": 11, "name": "対象OS", "value": ["Windows"] }, { "id": 13, "name": "顧客", "value": "ACME" } ]""")),
                RedmineFixture.Issue(i =>
                {
                    i["id"] = 2;
                    i["custom_fields"] = JsonNode.Parse("""[ { "id": 11, "name": "対象OS", "value": ["Linux", "Windows"] }, { "id": 13, "name": "顧客", "value": "" } ]""");
                })),
            ["total_count"] = 2,
            ["offset"] = 0,
            ["limit"] = 100,
        }));
        var f = await Run("fields", "--json");
        Assert.True(f.Code == 0, f.ToString());
        var json = f.Json;
        Assert.False(json["fields"]![0]!["detailed"]!.GetValue<bool>());
        Assert.Equal(2, json["sampled_issues"]!.GetValue<int>());
        Assert.Equal("*", Mock.Hit("GET", "/issues.json")[0].Query["status_id"]);
        var os = json["fields"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == 11)!;
        AssertJson("""["Linux", "Windows"]""", os["observed_values"]);
        Assert.True(os["observed_multiple"]!.GetValue<bool>());
        AssertJson("""["ACME"]""", json["fields"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == 13)!["observed_values"]);
        var r = await Run("issues", "create", "--subject", "x", "--field", "顧客=ACME", "--field", "対象OS=Windows", "--field", "対象OS=Linux", "--dry-run");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""[ { "id": 13, "value": "ACME" }, { "id": 11, "value": ["Windows", "Linux"] } ]""",
            r.Json["request"]!["body"]!["issue"]!["custom_fields"]);
    }

    [Fact]
    public async Task With_definitions_fields_does_not_sample_unless_sample_is_given()
    {
        var plain = await Run("fields", "--json");
        Assert.True(plain.Code == 0, plain.ToString());
        Assert.Null(plain.Json["sampled_issues"]);
        Assert.Empty(Mock.Hit("GET", "/issues.json"));
        var sampled = await Run("fields", "--sample", "10", "--json");
        Assert.True(sampled.Code == 0, sampled.ToString());
        Assert.Equal(1, sampled.Json["sampled_issues"]!.GetValue<int>());
        Assert.Equal("10", Mock.Hit("GET", "/issues.json")[0].Query["limit"]);
    }

    [Theory]
    [InlineData("issues create")]
    [InlineData("issues update")]
    [InlineData("time log")]
    [InlineData("api")]
    [InlineData("init")]
    public async Task Each_commands_help_exits_0_with_examples_and_the_global_options(string command)
    {
        var r = await Run([.. command.Split(' '), "--help"]);
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("例:", r.Stdout);
        Assert.Contains("--json", r.Stdout);
    }

    [Fact]
    public async Task Help_command_shows_a_subcommands_help()
    {
        var r = await Run("help", "issues", "create");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("--subject", r.Stdout);
        Assert.Contains("例:", r.Stdout);
    }

    [Fact]
    public async Task A_bad_argument_exits_2()
    {
        var r = await Run("issues", "list", "--no-such-option");
        Assert.Equal(2, r.Code);
        Assert.Contains("redmine issues list --help", r.Stderr);
        Assert.Empty(Mock.Requests);
    }

    [Fact]
    public async Task Version_prints_the_version()
    {
        var r = await Run("--version");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Matches(@"^\d+\.\d+\.\d+", r.Stdout);
    }

    [Fact]
    public async Task Guide_prints_the_markdown()
    {
        var r = await Run("guide");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("# redmine CLI", r.Stdout);
    }
}
