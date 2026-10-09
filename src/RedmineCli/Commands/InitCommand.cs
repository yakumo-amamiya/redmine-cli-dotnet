using System.CommandLine;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine init: writes .redmine.json, which fixes the write target for the directory tree below it.</summary>
internal static class InitCommand
{
    public static Command Create()
    {
        var url = new Option<string>("--url") { Description = "Redmine のルート URL (例: https://redmine.example.co.jp)", HelpName = "url" };
        var project = new Option<string>("--project") { Description = "プロジェクト識別子 (例: my-project)。数値 id ではない", HelpName = "identifier" };
        var env = new Option<string>("--env") { Description = "環境変数の接尾辞の別名 (例: HOSYU → REDMINE_API_KEY_HOSYU)。省略時は識別子から決める", HelpName = "name" };
        var dir = new Option<string>("--dir") { Description = "作成先ディレクトリ", HelpName = "path", DefaultValueFactory = _ => "." };
        var force = new Option<bool>("--force") { Description = $"既存の {ConfigFile.FileName} を上書きする" };
        var noVerify = new Option<bool>("--no-verify") { Description = "サーバーへ問い合わせずに書く (API キー不要。識別子の実在を確認しないので通常は使わない)" };
        var command = new Command("init", $"{ConfigFile.FileName} を作成する (リポジトリ直下で実行)。以後このディレクトリ配下での書き込み先が固定される")
        {
            url, project, env, dir, force, noVerify,
        }.WithNotes($$"""
            書くもの:
              { "url": "<Redmine のルート URL>", "project": "<プロジェクト識別子>", "env": "<接尾辞の別名 (任意)>" }
              トークンは書かない。このファイルはコミットしてよい。

            トークンの置き場所:
              環境変数 REDMINE_API_KEY_<接尾辞>。接尾辞は --env で指定した別名、無ければ識別子。
              どちらも大文字にし - を _ にする (例: --env hosyu → REDMINE_API_KEY_HOSYU、識別子 my-project → REDMINE_API_KEY_MY_PROJECT)。
              識別子が "plan" のように分かりにくいときに --env で覚えやすい名前を付ける。
              init は先にこの変数を読んでサーバーへ問い合わせるので、実行前に設定しておく。
              PowerShell: setx REDMINE_API_KEY_HOSYU "<キー>" のあと新しいシェルを開く。

            動作:
              既定ではサーバーに識別子の実在を問い合わせ、プロジェクト名を表示してから書く。
              上位ディレクトリに既に {{ConfigFile.FileName}} がある場合は注意を表示する (近い方が優先されるため)。

            例:
              redmine init --url https://redmine.example.co.jp --project my-project
              redmine init --url https://redmine.example.co.jp --project plan --env hosyu      # 変数名を REDMINE_API_KEY_HOSYU に
              redmine init --url https://redmine.example.co.jp --project my-project --force   # 書き直し
              redmine init --json ...   # {"file","url","project","env","name","api_key_env"} を出力

            終了コード: 0 作成 / 2 引数不足 / 3 識別子が存在しない・トークン未設定 / 1 既存ファイルあり (--force なし)
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var target = Path.GetFullPath(parse.GetValue(dir) ?? ".");
            var urlText = parse.GetValue(url) ?? Ask("Redmine のルート URL: ");
            var projectText = parse.GetValue(project) ?? Ask("プロジェクト識別子: ");
            var normalized = ConfigFile.NormalizeUrl(urlText);
            ConfigFile.ValidateIdentifier(projectText);
            var alias = parse.GetValue(env);

            var existing = ConfigFile.Find(target);
            if (existing is not null && Path.GetDirectoryName(existing) != target)
            {
                Info($"注意: 上位ディレクトリに {existing} があります。{target} 配下ではこれから作るファイルが優先されます。");
            }

            string? name = null;
            if (!parse.GetValue(noVerify))
            {
                var certEnv = EnvNames.ClientCert(projectText, alias);
                using var client = new RedmineClient(
                    normalized,
                    EnvNames.GetApiKey(projectText, alias),
                    ClientCertificates.Load(certEnv),
                    certEnv,
                    g.Verbose,
                    ExtraRoots.Load(),
                    cancellation);
                try
                {
                    var data = await client.GetAsync($"/projects/{Uri.EscapeDataString(projectText)}.json");
                    name = RedmineClient.Expect(data, "project").Name();
                }
                catch (HttpStatusException e) when (e.Status == 404)
                {
                    var candidates = await SuggestProjectsAsync(client, projectText);
                    throw new CliException(
                        $"プロジェクト識別子「{projectText}」は {normalized} に存在しないか、閲覧権限がありません",
                        Exit.Config,
                        string.Join("\n      ",
                            "識別子はプロジェクト名とは別の文字列で、ブラウザでプロジェクトを開いたときの URL (.../projects/<識別子>) に出ます。",
                            candidates.Count > 0
                                ? $"似た名前のプロジェクト: {string.Join(", ", candidates.Select(p => $"{p.Get("identifier").Text()} ({p.Name()})"))}"
                                : "閲覧できるプロジェクトの一覧は、いったん --no-verify で作ってから `redmine projects` で確認できます。",
                            "識別子を変えたら、API キーの環境変数名も変わる点に注意してください (例: REDMINE_API_KEY_HOSYU_KANRI)。"));
                }
            }

            var file = ConfigFile.Write(target, normalized, projectText, alias, parse.GetValue(force));
            var envName = EnvNames.ApiKey(projectText, alias);
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["file"] = file,
                    ["url"] = normalized,
                    ["project"] = projectText,
                    ["env"] = alias,
                    ["name"] = name,
                    ["api_key_env"] = envName,
                });
                return Exit.Ok;
            }
            Out($"作成しました: {file}");
            Out($"  url     : {normalized}");
            Out($"  project : {projectText}{(name is not null ? $" ({name})" : "")}");
            if (alias is not null)
            {
                Out($"  env     : {alias}");
            }
            Out($"  API キー: 環境変数 {envName}");
            if (parse.GetValue(noVerify))
            {
                Info($"サーバーには問い合わせていません。{envName} を設定し、`redmine me` で接続を確認してください。");
            }
            return Exit.Ok;
        });
        return command;
    }

    private static string Ask(string question)
    {
        if (Console.IsInputRedirected)
        {
            throw new CliException(
                "非対話環境では --url と --project の両方を指定してください",
                Exit.Usage,
                "例: redmine init --url https://redmine.example.co.jp --project my-project");
        }
        Console.Error.Write(question);
        return (Console.ReadLine() ?? "").Trim();
    }

    /// <summary>Up to 10 projects whose name or identifier contains the input (all of them when none does). Empty on failure.</summary>
    private static async Task<List<JsonNode>> SuggestProjectsAsync(RedmineClient client, string input)
    {
        try
        {
            var (items, _) = await client.ListAllAsync("/projects.json", "projects", max: 500);
            var q = input.ToLowerInvariant();
            var hit = items
                .Where(p => p.Get("identifier").Text().ToLowerInvariant().Contains(q, StringComparison.Ordinal)
                    || p.Name().ToLowerInvariant().Contains(q, StringComparison.Ordinal))
                .ToList();
            return (hit.Count > 0 ? hit : items).Take(10).ToList();
        }
        catch (CliException)
        {
            return [];
        }
    }
}
