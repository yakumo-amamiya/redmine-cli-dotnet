using System.CommandLine;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine projects: the projects the user can see (read only; it never changes the write target).</summary>
internal static class ProjectsCommand
{
    public static Command Create()
    {
        var limit = new Option<int>("--limit") { Description = "最大件数", HelpName = "n", DefaultValueFactory = _ => 200 };
        var search = new Option<string>("--search") { Description = "名前または識別子に含まれる文字列で絞り込む", HelpName = "text" };
        var command = new Command("projects", "閲覧できるプロジェクトの一覧 (読み取りのみ)。書き込み先の切り替えには使えない") { limit, search }.WithNotes("""
            対象プロジェクト (.redmine.json の project) には先頭に * を付けて表示する。
            init に渡す識別子を調べる用途で使う。ここで別のプロジェクトを見ても書き込み先は変わらない。

            出力 (--json):
              { "projects": [ { "id", "identifier", "name", "parent"?: {"id","name"}, "status", ... } ], "total": <閲覧可能な総数>, "target": "<識別子>" }

            例:
              redmine projects
              redmine projects --search 基盤
              redmine projects --json

            終了コード: 0 成功 / 3 設定不足 / 5 サーバーエラー
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var max = parse.GetValue(limit) is > 0 and var n ? n : 200;
            var (items, total) = await ctx.Client.ListAllAsync("/projects.json", "projects", max: max);
            var projects = items;
            if (parse.GetValue(search) is { Length: > 0 } text)
            {
                var q = text.ToLowerInvariant();
                projects = projects
                    .Where(p => p.Name().ToLowerInvariant().Contains(q, StringComparison.Ordinal)
                        || p.Get("identifier").Text().ToLowerInvariant().Contains(q, StringComparison.Ordinal))
                    .ToList();
            }
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["projects"] = new JsonArray([.. projects]),
                    ["total"] = total,
                    ["target"] = ctx.Config.Project,
                });
                return Exit.Ok;
            }
            Out(Table(projects,
                new Column<JsonNode>(" ", p => p.Get("identifier").Str() == ctx.Config.Project ? "*" : ""),
                new Column<JsonNode>("id", p => p.Get("id").Text(), Right: true),
                new Column<JsonNode>("識別子", p => p.Get("identifier").Text()),
                new Column<JsonNode>("名前", p => p.Name(), Max: 50),
                new Column<JsonNode>("親", p => p.Get("parent").Name(), Max: 30)));
            Out($"({projects.Count} 件表示 / 閲覧可能 {total} 件。* が書き込み先)");
            return Exit.Ok;
        });
        return command;
    }
}
