using System.Text.Json.Nodes;

namespace RedmineCli;

/// <summary>The global options, read from any command.</summary>
internal sealed record Globals(bool Json, bool Yes, bool DryRun, bool Verbose);

/// <summary>The project of .redmine.json as the server knows it.</summary>
internal sealed record TargetProject(long Id, string Name, string Identifier)
{
    public JsonObject ToJson() => new() { ["id"] = Id, ["name"] = Name, ["identifier"] = Identifier };
}

/// <summary>
/// The safety checks. Every write goes through here:
/// 1. the target is the project of .redmine.json and nothing else; no argument changes it;
/// 2. a write by issue id first asks the server which project the issue belongs to, and refuses another project's;
/// 3. before sending, the destination is shown, and --yes, a y/N answer or --dry-run is required.
/// </summary>
internal sealed class Context : IDisposable
{
    private TargetProject? _project;
    private JsonObject? _user;

    private Context(RedmineConfig config, RedmineClient client)
    {
        Config = config;
        Client = client;
        Lookup = new Lookups(this);
    }

    public RedmineConfig Config { get; }

    public RedmineClient Client { get; }

    public Lookups Lookup { get; }

    /// <summary>Reads .redmine.json, the project's API key and client certificate, and makes the client.</summary>
    public static Context Create(Globals globals, CancellationToken cancellation)
    {
        var config = ConfigFile.Load();
        var apiKey = EnvNames.GetApiKey(config.Project, config.Env);
        var certEnv = EnvNames.ClientCert(config);
        var extraRoots = ExtraRoots.Load();
        var certificate = ClientCertificates.Load(certEnv);
        var client = new RedmineClient(config.Url, apiKey, certificate, certEnv, globals.Verbose, extraRoots, cancellation);
        return new Context(config, client);
    }

    public void Dispose() => Client.Dispose();

    /// <summary>Looks up the identifier of .redmine.json on the server (once).</summary>
    public async Task<TargetProject> TargetProjectAsync()
    {
        if (_project is not null)
        {
            return _project;
        }
        JsonNode? data;
        try
        {
            data = await Client.GetAsync($"/projects/{Uri.EscapeDataString(Config.Project)}.json");
        }
        catch (HttpStatusException e) when (e.Status == 404)
        {
            throw new CliException(
                $".redmine.json の project「{Config.Project}」は {Config.Url} に存在しないか、閲覧権限がありません",
                Exit.Config,
                "`redmine projects` で識別子を確認し、`redmine init --force` で書き直してください。");
        }
        var p = RedmineClient.Expect(data, "project");
        _project = new TargetProject(p.Get("id").Long() ?? 0, p.Name(), p.Get("identifier").Str() ?? Config.Project);
        return _project;
    }

    public async Task<JsonObject> CurrentUserAsync() => _user ??= RedmineClient.Expect(await Client.GetAsync("/users/current.json"), "user");

    /// <summary>"123" or "#123".</summary>
    public static long ParseId(string? value, string label = "id")
    {
        var s = (value ?? "").StartsWith('#') ? value![1..] : value ?? "";
        if (s.Length == 0 || !s.All(char.IsAsciiDigit) || !long.TryParse(s, out var id))
        {
            throw new CliException($"{label} は数値で指定してください: {value}", Exit.Usage);
        }
        return id;
    }

    /// <summary>Gets an issue. include: journals, attachments, relations, children, watchers ...</summary>
    public async Task<JsonObject> GetIssueAsync(string id, params string[] include)
    {
        var issueId = ParseId(id, "issue id");
        var data = await Client.GetAsync($"/issues/{issueId}.json", new Query { { "include", include.Length > 0 ? string.Join(",", include) : null } });
        return RedmineClient.Expect(data, "issue");
    }

    /// <summary>Refuses (exit code 4) an issue outside the target project.</summary>
    public async Task<TargetProject> AssertInTargetAsync(JsonObject issue)
    {
        var target = await TargetProjectAsync();
        if (issue.Get("project").Get("id").Long() != target.Id)
        {
            var name = issue.Get("project").Get("name").Str() ?? "?";
            throw new CliException(
                $"#{issue.Get("id").Text()} はプロジェクト「{name}」に属しており、対象プロジェクト「{target.Name}」({target.Identifier}) ではありません",
                Exit.Refused,
                "書き込みは .redmine.json の project に属する issue に限定しています。別プロジェクトを扱うなら、そのリポジトリ (別の .redmine.json) で実行してください。");
        }
        return target;
    }

    /// <summary>Gets an issue and makes sure it is in the target project.</summary>
    public async Task<JsonObject> GetIssueInTargetAsync(string id, params string[] include)
    {
        var issue = await GetIssueAsync(id, include);
        await AssertInTargetAsync(issue);
        return issue;
    }

    /// <summary>
    /// The confirmation before a write.
    /// - The destination (server, project, action) goes to stderr.
    /// - With --dry-run the request to be sent goes to stdout as JSON and the answer is false (the caller sends nothing).
    /// - With --yes the answer is true.
    /// - On a terminal the user is asked y/N. Without a terminal and without --yes, exit code 4.
    /// </summary>
    public async Task<bool> ConfirmWriteAsync(string action, IEnumerable<string> lines, JsonNode request, Globals globals)
    {
        var target = await TargetProjectAsync();
        Output.Info("--- 書き込み先 ---");
        Output.Info($"サーバー     : {Config.Url}");
        Output.Info($"プロジェクト : {target.Name} ({target.Identifier})");
        Output.Info($"操作         : {action}");
        foreach (var line in lines)
        {
            Output.Info($"  {line}");
        }
        if (globals.DryRun)
        {
            Output.Info("--dry-run のため送信しません。送信予定の内容を stdout に出力します。");
            Output.PrintJson(new JsonObject
            {
                ["dry_run"] = true,
                ["target"] = new JsonObject { ["url"] = Config.Url, ["project"] = target.ToJson() },
                ["request"] = request,
            });
            return false;
        }
        if (globals.Yes)
        {
            return true;
        }
        if (Console.IsInputRedirected)
        {
            throw new CliException(
                "非対話環境のため確認できません。送信しませんでした",
                Exit.Refused,
                "上の書き込み先を確認したうえで --yes を付けて再実行してください。送信内容は --dry-run で事前に確認できます。");
        }
        Console.Error.Write("実行しますか? [y/N] ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer is not ("y" or "yes"))
        {
            throw new CliException("中止しました。送信していません", Exit.Refused);
        }
        return true;
    }
}
