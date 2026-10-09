using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using RedmineCli.Commands;

namespace RedmineCli;

/// <summary>The options every command takes (they can be written before or after the subcommand).</summary>
internal static class GlobalOptions
{
    public static readonly Option<bool> Json = new("--json")
    {
        Description = "結果を JSON で stdout に出す (機械処理・AI 向け。stdout には JSON 以外を出さない)",
        Recursive = true,
    };

    public static readonly Option<bool> Yes = new("--yes", "-y") { Description = "書き込み前の確認を省略する (非対話環境では必須)", Recursive = true };

    public static readonly Option<bool> DryRun = new("--dry-run") { Description = "書き込みを送信せず、送信予定の内容を JSON で stdout に出す", Recursive = true };

    public static readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "HTTP の往復を stderr に出す (API キーは出さない)", Recursive = true };

    public static Globals Of(ParseResult parse) => new(parse.GetValue(Json), parse.GetValue(Yes), parse.GetValue(DryRun), parse.GetValue(Verbose));
}

/// <summary>Text shown after a command's generated help: the output's shape, examples, exit codes.</summary>
internal static class HelpNotes
{
    private static readonly Dictionary<Command, string> Notes = new(ReferenceEqualityComparer.Instance);

    public static T WithNotes<T>(this T command, string notes)
        where T : Command
    {
        Notes[command] = notes;
        return command;
    }

    public static void Install(RootCommand root)
    {
        var help = root.Options.OfType<HelpOption>().Single();
        help.Description = "ヘルプを表示";
        help.Action = new NotesHelpAction((HelpAction)help.Action!);
    }

    private sealed class NotesHelpAction(HelpAction inner) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var code = inner.Invoke(parseResult);
            if (Notes.TryGetValue(parseResult.CommandResult.Command, out var notes))
            {
                var output = parseResult.InvocationConfiguration.Output;
                output.Write(notes.Trim('\n') + "\n");
                output.Flush();
            }
            return code;
        }
    }
}

internal static class Cli
{
    private const string Description = """
        Redmine を操作する CLI。
        書き込み先はリポジトリ直下の .redmine.json (url と project) で固定され、コマンド引数では変えられない。
        API キーはプロジェクト専用の環境変数 REDMINE_API_KEY_<識別子> からだけ読む。
        """;

    private const string RootNotes = """
        書き込み先の決まり方:
          カレントディレクトリから上位へ .redmine.json を探し、その url と project だけに書く。
          別プロジェクトへ書く手段は無い。別プロジェクトは別のリポジトリ (別の .redmine.json) で扱う。
          作業前に `redmine target` で向き先を確認する。

        読み取り (どのプロジェクトも可): target, doctor, me, projects, fields, issues list/show/files/download, time list, api GET
        書き込み (対象プロジェクトのみ): init, issues create/update/comment/attach, time log, api (GET 以外は --unsafe)

        書き込みの流れ:
          1. --dry-run で送信内容 (JSON) を確認する
          2. --yes を付けて実行する (非対話環境では --yes が無いと送信せず終了コード 4)

        環境変数:
          REDMINE_API_KEY_<接尾辞>  API アクセスキー (必須。引数では渡せず、出力にも出ない)。
                                   接尾辞は .redmine.json の env (無ければ project) を大文字にし - を _ にしたもの
                                   (例: env "hosyu" → REDMINE_API_KEY_HOSYU、project my-project → REDMINE_API_KEY_MY_PROJECT)。
                                   汎用の REDMINE_API_KEY は読まない。名前は `redmine target` で表示する
          REDMINE_CLIENT_CERT_<識別子>           クライアント証明書のパス (mTLS が必要な環境のみ)。.pfx/.p12 か PEM
          REDMINE_CLIENT_KEY_<識別子>            PEM の秘密鍵のパス (証明書ファイルに鍵が含まれていれば不要)
          REDMINE_CLIENT_CERT_PASSWORD_<識別子>  PFX や暗号化鍵のパスワード (任意)
          HTTPS_PROXY, NO_PROXY    社内プロキシ (例: http://user:pass@proxy.example.co.jp:8080)。
                                   設定しなければ Windows のプロキシ設定を使う
          REDMINE_EXTRA_CA_CERTS   プロキシが TLS を復号する環境で、社内ルート CA の PEM ファイル。
                                   Windows の証明書ストアに入っていれば不要 (Node 版の NODE_EXTRA_CA_CERTS も読む)

        終了コード:
          0 成功   1 その他の失敗 (接続、IO)   2 引数誤り   3 設定不足 (.redmine.json / API キーの環境変数)
          4 安全装置で拒否 (対象外プロジェクト、--yes なし、--unsafe なし)   5 サーバーがエラーを返した

        AI エージェント向けの手順書: redmine guide
        各コマンドの詳細        : redmine <command> --help   (例: redmine issues create --help)
        """;

    // No response files: a comment like "@someone 確認お願いします" must stay text, not be read as a file.
    internal static readonly ParserConfiguration ParserConfig = new() { ResponseFileTokenReplacer = null };

    public static RootCommand Build()
    {
        var root = new RootCommand(Description);
        // No [directives] either: an argument like "[WIP] ..." is text.
        root.Directives.Clear();
        root.Options.Add(GlobalOptions.Json);
        root.Options.Add(GlobalOptions.Yes);
        root.Options.Add(GlobalOptions.DryRun);
        root.Options.Add(GlobalOptions.Verbose);
        var version = root.Options.OfType<VersionOption>().Single();
        version.Aliases.Add("-V");
        version.Description = "バージョンを表示";
        root.Subcommands.Add(TargetCommand.CreateTarget());
        root.Subcommands.Add(TargetCommand.CreateMe());
        root.Subcommands.Add(DoctorCommand.Create());
        root.Subcommands.Add(InitCommand.Create());
        root.Subcommands.Add(ProjectsCommand.Create());
        root.Subcommands.Add(FieldsCommand.Create());
        root.Subcommands.Add(IssuesCommand.Create());
        root.Subcommands.Add(TimeCommand.Create());
        root.Subcommands.Add(ApiCommand.Create());
        root.Subcommands.Add(GuideCommand.Create());
        root.Subcommands.Add(CreateHelp(root));
        root.WithNotes(RootNotes);
        HelpNotes.Install(root);
        return root;
    }

    private static Command CreateHelp(RootCommand root)
    {
        var words = new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore, Description = "ヘルプを見るコマンド (例: issues create)" };
        var command = new Command("help", "コマンドのヘルプを表示") { words };
        command.SetAction(parse => root.Parse([.. parse.GetValue(words) ?? [], "--help"], ParserConfig).Invoke());
        return command;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var root = Build();
        var parse = root.Parse(args.Length == 0 ? ["--help"] : args, ParserConfig);
        if (parse.Action is ParseErrorAction)
        {
            foreach (var error in parse.Errors)
            {
                Output.Info($"エラー: {error.Message}");
            }
            Output.Info($"(使い方: {CommandPath(parse.CommandResult)} --help)");
            return Exit.Usage;
        }
        try
        {
            return await parse.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (CliException e)
        {
            Output.Info($"エラー: {e.Message}");
            if (e.Hint is { } hint)
            {
                Output.Info($"ヒント: {hint}");
            }
            return e.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Output.Info("中止しました。");
            return 130;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Output.Info($"エラー: {e.Message}");
            return Exit.Error;
        }
        catch (Exception e)
        {
            Output.Info($"エラー: {e}");
            return Exit.Error;
        }
    }

    private static string CommandPath(CommandResult result)
    {
        var names = new List<string>();
        for (SymbolResult? r = result; r is not null; r = r.Parent)
        {
            if (r is CommandResult c)
            {
                names.Insert(0, c.Command is RootCommand ? "redmine" : c.Command.Name);
            }
        }
        return string.Join(" ", names);
    }
}
