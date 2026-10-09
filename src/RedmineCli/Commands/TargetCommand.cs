using System.CommandLine;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine target (the local settings, no network) and redmine me (a connection check).</summary>
internal static class TargetCommand
{
    public static Command CreateTarget()
    {
        var command = new Command("target", "今の書き込み先 (.redmine.json の内容) を表示する。ネットワーク不要").WithNotes("""
            作業を始める前にまず実行し、意図したプロジェクトを向いているか確認する。

            出力 (--json):
              { "config_file": "<パス>", "url": "<サーバー>", "project": "<識別子>",
                "api_key_env": "<読む環境変数名>", "api_key_set": true|false,
                "client_cert": { "env": {"cert","key","password"}, "cert_file": "<パス>"|null, "key_file": "<パス>"|null,
                                 "cert_file_exists": true|false, "configured": true|false } }

            例:
              redmine target
              redmine target --json

            終了コード: 0 表示 / 3 .redmine.json が無い・不正
            """);
        command.SetAction(parse =>
        {
            var g = GlobalOptions.Of(parse);
            var config = ConfigFile.Load();
            var envName = EnvNames.ApiKey(config);
            var apiKeySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName));
            var certEnv = EnvNames.ClientCert(config);
            var certFile = Environment.GetEnvironmentVariable(certEnv.Cert)?.Trim();
            var keyFile = Environment.GetEnvironmentVariable(certEnv.Key)?.Trim();
            var certPath = string.IsNullOrEmpty(certFile) ? null : Path.GetFullPath(certFile);
            var keyPath = string.IsNullOrEmpty(keyFile) ? null : Path.GetFullPath(keyFile);
            var certExists = certPath is not null && File.Exists(certPath);
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["config_file"] = config.File,
                    ["url"] = config.Url,
                    ["project"] = config.Project,
                    ["env"] = config.Env,
                    ["api_key_env"] = envName,
                    ["api_key_set"] = apiKeySet,
                    ["client_cert"] = new JsonObject
                    {
                        ["env"] = new JsonObject { ["cert"] = certEnv.Cert, ["key"] = certEnv.Key, ["password"] = certEnv.Password },
                        ["cert_file"] = certPath,
                        ["key_file"] = keyPath,
                        ["cert_file_exists"] = certExists,
                        ["configured"] = certPath is not null,
                    },
                });
                return Exit.Ok;
            }
            Out($"設定ファイル : {config.File}");
            Out($"サーバー     : {config.Url}");
            Out($"プロジェクト : {config.Project}");
            Out($"変数の接尾辞 : {(config.Env is not null ? $"{config.Env} (.redmine.json の env)" : $"{config.Project} (識別子から。env で別名にできる)")}");
            Out($"API キー     : {(apiKeySet ? "設定済み" : "未設定")} (環境変数 {envName})");
            if (certPath is not null)
            {
                Out($"証明書       : {certPath}{(certExists ? "" : " (ファイルなし)")} (環境変数 {certEnv.Cert})");
                if (keyPath is not null)
                {
                    Out($"秘密鍵       : {keyPath} (環境変数 {certEnv.Key})");
                }
            }
            else
            {
                Out($"証明書       : 未設定 (mTLS が必要なら環境変数 {certEnv.Cert})");
            }
            if (!apiKeySet || (certPath is not null && !certExists))
            {
                Info("→ 未設定の変数は、このリポジトリで `redmine setup` を端末から実行すると対話で設定できます。");
            }
            return Exit.Ok;
        });
        return command;
    }

    public static Command CreateMe()
    {
        var command = new Command("me", "サーバーに接続し、自分のユーザー情報と対象プロジェクトを表示する (接続確認)").WithNotes("""
            url、トークン、プロキシ、プロジェクト識別子がすべて正しいことをまとめて確認できる。

            出力 (--json):
              { "user": { "id", "login", "firstname", "lastname", "mail", ... },
                "target": { "url", "project": { "id", "name", "identifier" } } }

            例:
              redmine me
              redmine me --json
              redmine me --verbose    # HTTP の往復を stderr に出す (接続トラブルの切り分け)

            終了コード: 0 成功 / 1 接続失敗 (プロキシ・証明書) / 3 設定不足・識別子不在 / 5 認証失敗など
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var user = await ctx.CurrentUserAsync();
            var project = await ctx.TargetProjectAsync();
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["user"] = user.DeepClone(),
                    ["target"] = new JsonObject { ["url"] = ctx.Config.Url, ["project"] = project.ToJson() },
                });
                return Exit.Ok;
            }
            Out($"ユーザー     : {user.Get("firstname").Text()} {user.Get("lastname").Text()} (login: {user.Get("login").Text()}, id: {user.Get("id").Text()})");
            Out($"サーバー     : {ctx.Config.Url}");
            Out($"プロジェクト : {project.Name} ({project.Identifier}, id: {project.Id})");
            Out($"設定ファイル : {ctx.Config.File}");
            return Exit.Ok;
        });
        return command;
    }
}
