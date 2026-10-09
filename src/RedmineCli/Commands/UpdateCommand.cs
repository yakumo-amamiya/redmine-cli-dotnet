using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine update: replaces this exe with a newer release.</summary>
internal static class UpdateCommand
{
    public static Command Create()
    {
        var check = new Option<bool>("--check") { Description = "新しい版があるか確かめるだけで、置き換えない" };
        var to = new Option<string>("--to") { Description = "入れる版 (例: 0.1.0)。省略時は最新版。前の版に戻すのにも使える", HelpName = "version" };
        var command = new Command("update", "redmine 自身を GitHub の Release の新しい版に更新する") { check, to }.WithNotes("""
            Release から redmine-win-x64.zip を落とし、SHA256SUMS と照合してから、今動いている redmine.exe と置き換える。
            置き場所は今の exe と同じ (install.ps1 で入れたなら %LOCALAPPDATA%\Programs\redmine-cli)。そこに書ければ管理者権限は要らない。
            古い exe は redmine.exe.old に退け、次に redmine を起動したときに消す。新しい exe が動かなければ元に戻す。
            github.com へ出る。社内プロキシと社内 CA は Redmine への接続と同じ設定を使う。
            開発用のビルド (dotnet run など) は置き換えない (--check はできる)。

            出力 (--json):
              --check のとき { "current", "latest", "update_available" }
              それ以外      { "current", "installed", "updated", "path" }

            例:
              redmine update
              redmine update --check
              redmine update --to 0.1.0     # 版を指定する

            終了コード: 0 更新した・最新だった / 1 接続できない・照合が合わない・置き換えられない
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var checkOnly = parse.GetValue(check);
            var target = Environment.ProcessPath ?? throw new CliException("この exe の場所が分かりません", Exit.Error);
            // A development build is an apphost with redmine.dll beside it; swapping the apphost alone would break it.
            // (RuntimeFeature.IsDynamicCodeSupported cannot tell: PublishAot turns it off in development builds too.)
            if (!checkOnly && File.Exists(Path.ChangeExtension(target, ".dll")))
            {
                throw new CliException("開発用のビルド (dotnet run など) は置き換えられません", Exit.Error,
                    "リリース版 (install.ps1 で入れたもの) で redmine update を実行してください。");
            }
            var current = Updater.CurrentVersion;
            using var updater = new Updater(Environment.GetEnvironmentVariable(Updater.ReleasesUrlEnv), g.Verbose, ExtraRoots.Load(), cancellation);
            var wanted = parse.GetValue(to)?.Trim().TrimStart('v');
            var version = string.IsNullOrEmpty(wanted) ? await updater.LatestVersionAsync() : wanted;

            if (checkOnly)
            {
                var available = Updater.IsNewer(version, current);
                if (g.Json)
                {
                    PrintJson(new JsonObject { ["current"] = current, ["latest"] = version, ["update_available"] = available });
                }
                else
                {
                    Out(available ? $"新しい版があります: {version} (今は {current})。redmine update で更新できます。" : $"最新です ({current})。");
                }
                return Exit.Ok;
            }

            var upToDate = string.IsNullOrEmpty(wanted) ? !Updater.IsNewer(version, current) : string.Equals(version, current, StringComparison.OrdinalIgnoreCase);
            // Made before the swap: afterwards this exe's file is gone from its path, so nothing new may be loaded.
            var report = g.Json
                ? Json.Format(new JsonObject { ["current"] = current, ["installed"] = version, ["updated"] = !upToDate, ["path"] = target })
                : upToDate ? $"すでに {current} です (最新)。" : $"redmine を {current} から {version} に更新しました: {target}";
            if (!upToDate)
            {
                var work = Path.Combine(Path.GetTempPath(), $"redmine-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(work);
                try
                {
                    Info($"{version} を落としています ({Updater.Asset})...");
                    var exe = await updater.DownloadAsync(version, work);
                    try
                    {
                        Updater.Install(target, exe, path => RunsAs(path, version));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        throw new CliException($"{target} を置き換えられません ({e.GetType().Name}): {e.Message}", Exit.Error,
                            "置き場所に書き込めるか確認してください。管理者権限が要る場所なら、install.ps1 で %LOCALAPPDATA% に入れ直してください。");
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(work, recursive: true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A temp folder left behind is harmless.
                    }
                }
            }
            Console.Out.Write(report + "\n");
            return Exit.Ok;
        });
        return command;
    }

    /// <summary>Whether the exe at path starts and reports the version.</summary>
    private static bool RunsAs(string path, string version)
    {
        var start = new ProcessStartInfo(path, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0 && output.Result.Trim() == version;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
