using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine doctor: checks the settings, the certificate and the connection step by step and shows where it stops.</summary>
internal static class DoctorCommand
{
    private const string Ok = "OK";
    private const string Ng = "NG";
    private const string Skip = "--";

    internal sealed record Step(string Name, string Status, string Detail, string? Hint);

    public static Command Create()
    {
        var offline = new Option<bool>("--offline") { Description = "ネットワークに出ず、設定と証明書ファイルの検査だけ行う" };
        var command = new Command("doctor", "設定・証明書・接続を段階ごとに検査し、どこで止まっているかを表示する") { offline }.WithNotes("""
            検査の順序:
              1. .redmine.json            見つかるか、内容が妥当か
              2. API キーの環境変数        設定されているか (値は表示しない)
              3. 証明書ファイル            パスが指す先が読めるか
              4. 証明書の読み込み          パスフレーズで復号できるか、形式が正しいか (ネットワーク不要)
              5. 証明書の内容              subject / issuer / 有効期限
              6. 経路                      直接か、どのプロキシを通るか (環境変数か Windows の設定)
              7. 接続と認証                TLS → 経路 (プロキシ・SSO) → Redmine の API キー、のどこで止まるか
              8. 対象プロジェクト          識別子が存在するか

            出力 (--json): { "ok": bool, "steps": [ { "name", "status": "OK"|"NG"|"--", "detail", "hint" } ] }

            例:
              redmine doctor
              redmine doctor --offline     # 証明書とパスフレーズだけ確かめる

            終了コード: 0 すべて OK / 1 いずれかが NG
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var (ok, steps) = await RunChecksAsync(parse.GetValue(offline), Environment.GetEnvironmentVariable, cancellation);
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["ok"] = ok,
                    ["steps"] = new JsonArray([.. steps.Select(s => (JsonNode)new JsonObject
                    {
                        ["name"] = s.Name,
                        ["status"] = s.Status,
                        ["detail"] = s.Detail,
                        ["hint"] = s.Hint,
                    })]),
                });
            }
            else
            {
                Print(steps, ok, Out);
            }
            return ok ? Exit.Ok : Exit.Error;
        });
        return command;
    }

    public static void Print(IEnumerable<Step> steps, bool ok, Action<string> write)
    {
        foreach (var s in steps)
        {
            write($"{s.Status} {s.Name}: {s.Detail}");
            if (s.Hint is not null)
            {
                write($"      → {s.Hint}");
            }
        }
        write(ok ? "すべて OK です" : "最初の NG が原因です。その行のヒントを見てください");
    }

    /// <summary>All the checks, reading the variables through getEnv (setup checks the values it is about to write).</summary>
    public static async Task<(bool Ok, List<Step> Steps)> RunChecksAsync(bool offline, Func<string, string?> getEnv, CancellationToken cancellation)
    {
        var steps = new List<Step>();
        RedmineConfig? config = null;
        try
        {
            config = ConfigFile.Load();
            steps.Add(new Step(".redmine.json", Ok,
                $"{config.File} → {config.Url} / {config.Project}{(config.Env is not null ? $" (環境変数の接尾辞: {config.Env})" : "")}", null));
        }
        catch (CliException e)
        {
            steps.Add(new Step(".redmine.json", Ng, e.Message, e.Hint));
        }
        string? apiKey = null;
        var certificate = (Ok: true, Value: (ClientCertificate?)null);
        if (config is not null)
        {
            try
            {
                apiKey = EnvNames.GetApiKey(config.Project, config.Env, getEnv, config.Url);
                steps.Add(new Step("API キー", Ok, $"環境変数 {EnvNames.ApiKey(config)} は設定済み", null));
            }
            catch (CliException e)
            {
                steps.Add(new Step("API キー", Ng, e.Message, e.Hint));
            }
            certificate = CheckCertificate(config, steps, getEnv);
            try
            {
                steps.Add(new Step("経路", Ok, EnvProxy.DescribeRoute(new Uri(config.Url + "/"), getEnv), null));
            }
            catch (CliException e)
            {
                steps.Add(new Step("経路", Ng, e.Message, e.Hint));
                certificate.Ok = false;
                certificate.Value?.Dispose();
                certificate.Value = null;
            }
        }
        try
        {
            if (!offline)
            {
                if (config is not null && apiKey is not null && certificate.Ok)
                {
                    await CheckConnectionAsync(config, apiKey, certificate.Value, steps, getEnv, cancellation);
                    certificate.Value = null; // the client disposed it
                }
                else
                {
                    steps.Add(new Step("接続と認証", Skip, "前の段階が NG のため省略", null));
                }
            }
        }
        finally
        {
            certificate.Value?.Dispose();
        }
        return (steps.All(s => s.Status != Ng), steps);
    }

    /// <summary>Reads and decodes the certificate locally (no network).</summary>
    private static (bool Ok, ClientCertificate? Value) CheckCertificate(RedmineConfig config, List<Step> steps, Func<string, string?> getEnv)
    {
        var names = EnvNames.ClientCert(config);
        ClientCertFiles? files;
        try
        {
            files = ClientCertificates.Read(names, getEnv);
        }
        catch (CliException e)
        {
            steps.Add(new Step("証明書ファイル", Ng, e.Message, e.Hint));
            return (false, null);
        }
        if (files is null)
        {
            steps.Add(new Step("証明書", Skip, $"未設定 (mTLS が必要なら `redmine setup` で設定する。変数は {names.Cert} など)", null));
            return (true, null);
        }
        steps.Add(new Step("証明書ファイル", Ok,
            $"{files.CertFile} ({files.Format.ToUpperInvariant()}){(files.KeyFile is not null ? $", 鍵 {files.KeyFile}" : "")}", null));
        ClientCertificate certificate;
        try
        {
            certificate = ClientCertificates.Decode(files);
        }
        catch (CertLoadException e)
        {
            steps.Add(new Step("証明書の読み込み", Ng, e.Detail, e.Hint));
            return (false, null);
        }
        steps.Add(new Step("証明書の読み込み", Ok, files.Password is not null ? "パスフレーズで復号できました" : "パスフレーズなしで読めました", null));
        var content = DescribeCertificate(certificate.Certificate, out var expired);
        steps.Add(new Step("証明書の内容", expired ? Ng : Ok, content, expired ? "IT 部門に証明書の再発行を依頼してください。" : null));
        if (expired)
        {
            certificate.Dispose();
            return (false, null);
        }
        return (true, certificate);
    }

    /// <summary>"subject=... / issuer=... / 有効期限 ..." for a certificate.</summary>
    public static string DescribeCertificate(X509Certificate2 x, out bool expired)
    {
        expired = x.NotAfter.ToUniversalTime() < DateTime.UtcNow;
        var validTo = x.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return $"subject={x.Subject} / issuer={x.Issuer} / 有効期限 {validTo}{(expired ? " (期限切れ)" : "")}";
    }

    /// <summary>Connects for real and tells which stage stops it.</summary>
    private static async Task CheckConnectionAsync(
        RedmineConfig config, string apiKey, ClientCertificate? certificate, List<Step> steps, Func<string, string?> getEnv, CancellationToken cancellation)
    {
        X509Certificate2Collection extraRoots;
        try
        {
            extraRoots = ExtraRoots.Load(getEnv);
        }
        catch (CliException e)
        {
            certificate?.Dispose();
            steps.Add(new Step("接続と認証", Ng, e.Message, e.Hint));
            return;
        }
        using var client = new RedmineClient(config.Url, apiKey, certificate, EnvNames.ClientCert(config), verbose: false, extraRoots, cancellation);
        try
        {
            var me = RedmineClient.Expect(await client.GetAsync("/users/current.json"), "user");
            steps.Add(new Step("接続と認証", Ok, $"{config.Url} に到達し、API キーで {me.Get("login").Text()} として認証できました", null));
        }
        catch (HttpStatusException e)
        {
            var kind = e.Status switch
            {
                401 => "TLS と経路は通り、Redmine が API キーを拒否しました",
                403 => "TLS と経路は通り、Redmine が権限を拒否しました (REST API 無効の可能性)",
                404 => "TLS と経路は通りましたが、その URL に Redmine の API がありません",
                _ => $"TLS と経路は通り、サーバーが HTTP {e.Status} を返しました",
            };
            steps.Add(new Step("接続と認証", Ng, kind, e.Hint));
            return;
        }
        catch (CliException e)
        {
            steps.Add(new Step("接続と認証", Ng, e.Message, e.Hint));
            return;
        }
        try
        {
            var p = RedmineClient.Expect(await client.GetAsync($"/projects/{Uri.EscapeDataString(config.Project)}.json"), "project");
            steps.Add(new Step("対象プロジェクト", Ok, $"{p.Name()} ({p.Get("identifier").Text()}, id {p.Get("id").Text()})", null));
        }
        catch (CliException e)
        {
            steps.Add(new Step("対象プロジェクト", Ng, e.Message, e.Hint));
        }
    }
}
