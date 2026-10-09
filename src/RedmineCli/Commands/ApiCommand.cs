using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine api: calls the REST API directly (the way out for what the other commands do not cover).</summary>
internal static class ApiCommand
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static Command Create()
    {
        var method = new Argument<string>("method") { Description = "GET | POST | PUT | PATCH | DELETE" };
        var path = new Argument<string>("path") { Description = ".redmine.json の url からの相対パス (例: /issues.json?limit=5)" };
        var data = new Option<string>("--data") { Description = "リクエストボディ (JSON 文字列)", HelpName = "json" };
        var dataFile = new Option<string>("--data-file") { Description = "リクエストボディを JSON ファイルから読む (- で stdin)", HelpName = "file" };
        var unsafeOption = new Option<bool>("--unsafe") { Description = "GET 以外を許可する。CLI はプロジェクトの制約を検証できないので、宛先は自分で確認すること" };
        var command = new Command("api", "Redmine REST API を直接呼ぶ (未実装の操作の逃げ道)。GET 以外は --unsafe が必要")
        {
            method, path, data, dataFile, unsafeOption,
        }.WithNotes("""
            path は .redmine.json の url からの相対パス。先頭の / と .json 拡張子を含める。クエリ文字列も付けられる。
            結果は常に JSON で stdout に出す (--json 不要)。

            安全装置:
              GET は自由。POST / PUT / PATCH / DELETE は --unsafe が無ければ終了コード 4 で拒否する。
              --unsafe を付けても、宛先表示と --yes / 対話確認 / --dry-run は通る。
              ただし対象プロジェクトの検証はできない (ボディの project_id や issue の所属を CLI は解釈しない)。
              issues / time のサブコマンドで足りるならそちらを使うこと。

            例:
              redmine api GET /issues.json?assigned_to_id=me&limit=5
              redmine api GET /projects/my-project/versions.json
              redmine api GET /issues/123.json?include=watchers
              redmine api GET /enumerations/issue_priorities.json
              redmine api POST /issues/123/watchers.json --data '{"user_id": 5}' --unsafe --yes
              redmine api DELETE /issues/123/watchers/5.json --unsafe --dry-run

            終了コード: 0 成功 / 2 引数誤り / 4 --unsafe なし・確認できず / 5 サーバーエラー
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var methodArg = parse.GetValue(method)!;
            var verb = methodArg.ToUpperInvariant();
            if (!Methods.Contains(verb))
            {
                throw new CliException($"method は {string.Join(" / ", Methods)} のいずれか: {methodArg}", Exit.Usage);
            }
            var apiPath = parse.GetValue(path)!;
            if (!apiPath.StartsWith('/'))
            {
                throw new CliException($"path は / で始めてください: {apiPath}", Exit.Usage);
            }
            var raw = Input.ReadTextOption(parse.GetValue(data), parse.GetValue(dataFile), "data");
            JsonNode? body = null;
            if (raw is not null)
            {
                try
                {
                    body = Json.Parse(raw);
                }
                catch (JsonException e)
                {
                    throw new CliException($"ボディが JSON として解釈できません: {e.Message}", Exit.Usage);
                }
                if (verb == "GET")
                {
                    throw new CliException("GET にはボディを付けられません", Exit.Usage, "クエリ文字列で path に書いてください (例: /issues.json?limit=5)。");
                }
            }
            using var ctx = Context.Create(g, cancellation);
            if (verb != "GET")
            {
                if (!parse.GetValue(unsafeOption))
                {
                    throw new CliException(
                        $"{verb} は --unsafe が無いと実行できません",
                        Exit.Refused,
                        "api コマンドは宛先プロジェクトを検証できません。issues / time のサブコマンドで代替できないか確認し、必要なら --unsafe --dry-run で内容を見てから --yes で実行してください。");
                }
                await ctx.TargetProjectAsync();
                var shown = body is null ? null : Json.Serialize(body);
                var request = new JsonObject { ["method"] = verb, ["path"] = apiPath, ["body"] = body?.DeepClone() };
                List<string> lines = shown is null ? [] : [$"body: {(shown.Length > 200 ? shown[..200] : shown)}"];
                if (!await ctx.ConfirmWriteAsync($"API {verb} {apiPath} (プロジェクトの検証なし)", lines, request, g))
                {
                    return Exit.Ok;
                }
            }
            var result = await ctx.Client.RequestAsync(verb, apiPath, json: body);
            if (result is null)
            {
                Info($"{verb} {apiPath}: 応答ボディなし (成功)");
                PrintJson(new JsonObject { ["ok"] = true });
                return Exit.Ok;
            }
            PrintJson(result);
            return Exit.Ok;
        });
        return command;
    }
}
