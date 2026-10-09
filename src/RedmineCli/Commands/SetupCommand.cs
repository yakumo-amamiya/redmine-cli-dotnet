using System.CommandLine;
using System.Text;

namespace RedmineCli.Commands;

/// <summary>What setup asks through: the terminal in the exe, a script in the tests.</summary>
internal interface ISetupConsole
{
    /// <summary>A line typed by the user; null at the end of input.</summary>
    string? ReadLine(string prompt);

    /// <summary>Text typed without echo (a key, a password); null at the end of input.</summary>
    string? ReadSecret(string prompt);

    void Write(string line);
}

/// <summary>The user's own environment variables, the ones new shells start with (HKCU\Environment on Windows).</summary>
internal interface IUserEnvironment
{
    string? Get(string name);

    /// <summary>Sets a variable; null deletes it.</summary>
    void Set(string name, string? value);
}

/// <summary>redmine setup: asks for the user's API key and client certificate and writes them to the project's own variables.</summary>
internal static class SetupCommand
{
    public static Command Create()
    {
        var command = new Command("setup", "このリポジトリ用の自分の API キーとクライアント証明書を、対話でユーザー環境変数に設定する (端末から実行)")
            .WithNotes("""
                既に .redmine.json があるリポジトリ (誰かが init 済み) で、自分の分の設定をするためのコマンド。

                聞くこと:
                  1. API キー            → REDMINE_API_KEY_<接尾辞>。画面に出さずに受け取る
                  2. クライアント証明書  → REDMINE_CLIENT_CERT_<接尾辞> / REDMINE_CLIENT_KEY_<接尾辞> / REDMINE_CLIENT_CERT_PASSWORD_<接尾辞>。
                                           mTLS が要る環境だけ。パスワードはその場で証明書を開いて確かめる
                書く前に内容を見せて確認する (--yes で省略、--dry-run なら書かない)。書いたあと doctor と同じ検査で接続を確かめる。
                値はユーザー環境変数に書き、新しく開いたシェルから使える。.redmine.json は書き換えない。
                端末から実行したときだけ動く。AI エージェントやパイプからの実行 (非対話) は終了コード 4 で断る。
                .redmine.json がまだ無いときは、先に redmine init。

                例:
                  redmine setup
                  redmine setup --dry-run     # 聞くだけで、書かない

                終了コード: 0 設定して接続も OK / 1 接続の検査で NG / 3 .redmine.json が無い / 4 非対話・中止
                """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var config = ConfigFile.Load();
            if (!OperatingSystem.IsWindows())
            {
                throw new CliException("setup は Windows 用です", Exit.Usage, ManualHint(config, "export"));
            }
            if (Console.IsInputRedirected)
            {
                throw new CliException(
                    "setup は端末から対話で実行してください。非対話の実行 (AI エージェント、パイプ) では使えません",
                    Exit.Refused,
                    ManualHint(config, "setx"));
            }
            var flow = new SetupFlow(config, new TerminalConsole(), new WindowsUserEnvironment());
            return await flow.RunAsync(g, async getEnv =>
            {
                var (ok, steps) = await DoctorCommand.RunChecksAsync(offline: false, getEnv, cancellation);
                DoctorCommand.Print(steps, ok, Output.Info);
                return ok;
            });
        });
        return command;
    }

    /// <summary>How to set the variables by hand, for when setup cannot ask.</summary>
    private static string ManualHint(RedmineConfig config, string tool)
    {
        var names = EnvNames.ClientCert(config);
        string Line(string name, string value) => tool == "setx" ? $"setx {name} \"{value}\"" : $"export {name}=\"{value}\"";
        return string.Join("\n      ",
            "人が端末で redmine setup を実行してください。手で設定するなら (設定後に新しいシェルを開く):",
            Line(EnvNames.ApiKey(config), "<API キー>") + $"   # キーは {config.Url}/my/account の「APIアクセスキー」",
            Line(names.Cert, "<証明書ファイルのパス>") + "   # mTLS が要る環境だけ。.pfx / .p12 か PEM",
            Line(names.Key, "<PEM の秘密鍵のパス>") + "   # PEM で鍵が別ファイルのときだけ",
            Line(names.Password, "<パスワード>") + "   # PFX のパスワードか、暗号化された鍵のパスフレーズがあるときだけ");
    }

    private sealed class TerminalConsole : ISetupConsole
    {
        public string? ReadLine(string prompt)
        {
            Console.Error.Write(prompt);
            return Console.ReadLine();
        }

        public string? ReadSecret(string prompt)
        {
            Console.Error.Write(prompt);
            var text = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    text.Append(key.KeyChar);
                }
            }
            Console.Error.Write("\n");
            return text.ToString();
        }

        public void Write(string line) => Output.Info(line);
    }

    private sealed class WindowsUserEnvironment : IUserEnvironment
    {
        // The user's saved value first (what new shells get), then this process's (a machine-wide or session variable).
        public string? Get(string name) =>
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { Length: > 0 } saved ? saved : Environment.GetEnvironmentVariable(name);

        // Also tells running programs (Explorer, new terminals) that the environment changed.
        public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }
}

/// <summary>
/// The dialogue of redmine setup. It only collects values and writes the project's own variables; the CLI still reads the
/// key from the variable alone, and no argument takes it.
/// </summary>
internal sealed class SetupFlow(RedmineConfig config, ISetupConsole io, IUserEnvironment env)
{
    // Variables to write, in the order they were decided; null deletes.
    private readonly List<KeyValuePair<string, string?>> _changes = [];
    private readonly HashSet<string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The value a variable will have once the changes are written.</summary>
    public string? Effective(string name)
    {
        foreach (var (key, value) in _changes)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return env.Get(name);
    }

    /// <param name="check">Checks the connection with the variables read through the given function (doctor's checks).</param>
    public async Task<int> RunAsync(Globals g, Func<Func<string, string?>, Task<bool>> check)
    {
        io.Write("redmine setup: このリポジトリの書き込み先に合わせて、自分の API キーとクライアント証明書を設定します。");
        io.Write($"  設定ファイル : {config.File}");
        io.Write($"  サーバー     : {config.Url}");
        io.Write($"  プロジェクト : {config.Project}");
        io.Write("  入力した値は、このプロジェクト専用のユーザー環境変数に書きます (キーとパスワードは画面に出しません)。");
        io.Write("");
        AskApiKey(EnvNames.ApiKey(config));
        io.Write("");
        AskCertificate(EnvNames.ClientCert(config));
        io.Write("");
        if (_changes.Count == 0)
        {
            io.Write("変更はありません。");
        }
        else
        {
            io.Write("書き込む内容 (ユーザー環境変数):");
            var width = _changes.Max(c => c.Key.Length);
            foreach (var (name, value) in _changes)
            {
                var shown = value is null ? "(消す)" : _secrets.Contains(name) ? "(入力した値。表示しません)" : value;
                io.Write($"  {name.PadRight(width)} = {shown}");
            }
            if (g.DryRun)
            {
                io.Write("--dry-run のため書きません。");
                return Exit.Ok;
            }
            if (!g.Yes && !Confirm("書きますか? [y/N] "))
            {
                throw new CliException("中止しました。何も書いていません", Exit.Refused);
            }
            foreach (var (name, value) in _changes)
            {
                env.Set(name, value);
            }
            io.Write("書きました。新しく開いたシェルから使えます (今開いているシェルには反映されません)。");
        }
        io.Write("");
        io.Write("接続を確かめます (redmine doctor と同じ検査):");
        return await check(Effective) ? Exit.Ok : Exit.Error;
    }

    private void Change(string name, string? value, bool secret = false)
    {
        _changes.RemoveAll(c => string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase));
        _changes.Add(new KeyValuePair<string, string?>(name, value));
        if (secret)
        {
            _secrets.Add(name);
        }
    }

    /// <summary>Deletes a variable only if it is set (so the summary shows no deletion of nothing).</summary>
    private void Delete(string name)
    {
        if (!string.IsNullOrWhiteSpace(env.Get(name)))
        {
            Change(name, null);
        }
    }

    private string Ask(string prompt) =>
        io.ReadLine(prompt) ?? throw new CliException("入力が終わったので中止しました。何も書いていません", Exit.Refused);

    private string AskSecret(string prompt) =>
        io.ReadSecret(prompt) ?? throw new CliException("入力が終わったので中止しました。何も書いていません", Exit.Refused);

    private bool Confirm(string prompt) => Ask(prompt).Trim().ToLowerInvariant() is "y" or "yes";

    /// <summary>A path as typed or pasted: surrounding quotes (Explorer's "copy as path") dropped, %VARIABLES% expanded.</summary>
    private static string CleanPath(string text)
    {
        var s = text.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            s = s[1..^1].Trim();
        }
        return s.Length == 0 ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(s));
    }

    private void AskApiKey(string name)
    {
        var current = env.Get(name);
        var isSet = !string.IsNullOrWhiteSpace(current);
        io.Write($"[1/2] API キー (環境変数 {name}): {(isSet ? "設定済み" : "未設定")}");
        io.Write($"  キーは Redmine の「個人設定」の右側にある「APIアクセスキー」の「表示」で見られます: {config.Url}/my/account");
        var prompt = isSet
            ? "  変えるなら新しいキーを貼り付けて Enter (画面には出ません。空のまま Enter で今のまま): "
            : "  API キーを貼り付けて Enter (画面には出ません。空のまま Enter で飛ばす): ";
        while (true)
        {
            var key = AskSecret(prompt).Trim();
            if (key.Length == 0)
            {
                io.Write(isSet ? "  今のままにします。" : "  飛ばしました (キーが無いと接続できません)。");
                return;
            }
            if (key.Any(char.IsWhiteSpace))
            {
                io.Write("  キーの途中に空白があります。貼り付け直してください。");
                prompt = "  API キー (空のまま Enter で飛ばす): ";
                continue;
            }
            Change(name, key, secret: true);
            io.Write($"  受け取りました ({key.Length} 文字)。");
            return;
        }
    }

    private void AskCertificate(ClientCertEnvNames names)
    {
        var current = env.Get(names.Cert);
        var isSet = !string.IsNullOrWhiteSpace(current);
        io.Write($"[2/2] クライアント証明書 (mTLS): {(isSet ? $"設定済み ({current})" : "未設定")}");
        if (!isSet)
        {
            io.Write("  Redmine の前段がクライアント証明書を求める環境でだけ要ります。ブラウザで Redmine を開くときに証明書を選ぶ画面が出ないなら不要です。");
            if (!Confirm("  設定しますか? [y/N] "))
            {
                return;
            }
        }
        else
        {
            var answer = Ask("  設定し直すなら y、設定を消すなら d (そのままなら空のまま Enter): ").Trim().ToLowerInvariant();
            if (answer is "d" or "delete")
            {
                Change(names.Cert, null);
                Delete(names.Key);
                Delete(names.Password);
                io.Write("  証明書の設定を消します。");
                return;
            }
            if (answer is not ("y" or "yes"))
            {
                return;
            }
        }

        string certPath;
        while (true)
        {
            certPath = CleanPath(Ask("  証明書ファイルのパス (.pfx / .p12、または PEM): "));
            if (certPath.Length == 0)
            {
                io.Write("  証明書の設定はやめます。");
                return;
            }
            if (File.Exists(certPath))
            {
                break;
            }
            io.Write($"  ファイルがありません: {certPath}");
        }
        var isPfx = certPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || certPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);
        string? keyPath = null;
        if (!isPfx)
        {
            while (true)
            {
                var typed = CleanPath(Ask("  PEM の秘密鍵ファイルのパス (証明書のファイルに鍵も入っているなら空のまま Enter): "));
                if (typed.Length == 0 || File.Exists(typed))
                {
                    keyPath = typed.Length == 0 ? null : typed;
                    break;
                }
                io.Write($"  ファイルがありません: {typed}");
            }
        }
        var password = AskSecret(isPfx
            ? "  PFX のパスワード (無ければ空のまま Enter。画面には出ません): "
            : "  鍵のパスフレーズ (暗号化されていなければ空のまま Enter。画面には出ません): ");
        for (var attempt = 1; ; attempt++)
        {
            var given = new Dictionary<string, string?> { [names.Cert] = certPath, [names.Key] = keyPath, [names.Password] = password };
            try
            {
                using var certificate = ClientCertificates.Decode(ClientCertificates.Read(names, name => given.GetValueOrDefault(name))!);
                io.Write($"  読めました: {DoctorCommand.DescribeCertificate(certificate.Certificate, out var expired)}");
                if (expired)
                {
                    io.Write("  この証明書は期限切れです。IT 部門に再発行を頼んでください (設定はします)。");
                }
                break;
            }
            catch (CertLoadException e) when (e.Problem is CertProblem.WrongPassword or CertProblem.MissingPassword)
            {
                io.Write($"  {e.Detail}。");
                if (attempt == 3)
                {
                    io.Write("  3 回合わなかったので、証明書の設定はやめます。");
                    return;
                }
                password = AskSecret("  もう一度入力 (無ければ空のまま Enter。画面には出ません): ");
            }
            catch (CliException e)
            {
                io.Write($"  読めません: {e.Message}");
                if (e.Hint is not null)
                {
                    io.Write($"  → {e.Hint}");
                }
                io.Write("  証明書の設定はやめます。");
                return;
            }
        }
        Change(names.Cert, certPath);
        if (keyPath is not null)
        {
            Change(names.Key, keyPath);
        }
        else
        {
            Delete(names.Key);
        }
        if (password.Length > 0)
        {
            Change(names.Password, password, secret: true);
        }
        else
        {
            Delete(names.Password);
        }
    }
}
