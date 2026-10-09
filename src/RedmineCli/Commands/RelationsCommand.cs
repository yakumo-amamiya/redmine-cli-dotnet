using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>A Redmine relation type, its other side, and the name Redmine's Japanese screens give it.</summary>
internal sealed record RelationType(string Name, string Reverse, string Label);

/// <summary>redmine issues relations / relate / unrelate: the related issues of an issue.</summary>
internal static class RelationsCommand
{
    // Redmine stores each relation once, in one direction (A blocks B); B sees it as "blocked". Labels are Redmine's ja.yml.
    public static readonly RelationType[] Types =
    [
        new("relates", "relates", "関連している"),
        new("duplicates", "duplicated", "次のチケットと重複"),
        new("duplicated", "duplicates", "次のチケットが重複"),
        new("blocks", "blocked", "ブロック先"),
        new("blocked", "blocks", "ブロック元"),
        new("precedes", "follows", "次のチケットに先行"),
        new("follows", "precedes", "次のチケットに後続"),
        new("copied_to", "copied_from", "コピー先"),
        new("copied_from", "copied_to", "コピー元"),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["related"] = "relates",
        ["relates_to"] = "relates",
        ["duplicated_by"] = "duplicated",
        ["blocked_by"] = "blocked",
        ["copies"] = "copied_to",
    };

    private const string TypesHelp = """
        関連の種類 (--type。<id> から見た向きで書く):
          relates      関連している          (既定)
          duplicates   次のチケットと重複    <id> が <相手> と重複している (<相手> が残す方)
          duplicated   次のチケットが重複    <相手> が <id> と重複している
          blocks       ブロック先            <id> が終わるまで <相手> を終えられない
          blocked      ブロック元            <相手> が終わるまで <id> を終えられない
          precedes     次のチケットに先行    <id> の後に <相手> (--delay で間の日数)
          follows      次のチケットに後続    <相手> の後に <id> (--delay で間の日数)
          copied_to    コピー先
          copied_from  コピー元
        """;

    public static RelationType Find(string name) =>
        Types.FirstOrDefault(t => t.Name == name) ?? new RelationType(name, name, name);

    /// <summary>A --type value to a relation type; - is read as _ ("blocked-by").</summary>
    public static RelationType Parse(string input)
    {
        var name = input.Trim().Replace('-', '_').ToLowerInvariant();
        name = Aliases.GetValueOrDefault(name, name);
        return Types.FirstOrDefault(t => t.Name == name)
            ?? throw new CliException($"関連の種類「{input}」はありません", Exit.Usage, $"選べる種類: {string.Join(", ", Types.Select(t => $"{t.Name} ({t.Label})"))}");
    }

    /// <summary>A relation as seen from one of its issues: the type turned to that side, and the other issue.</summary>
    internal sealed record SeenRelation(long Id, RelationType Type, long Other, JsonNode? Delay);

    public static SeenRelation Seen(long issueId, JsonNode relation)
    {
        var type = Find(relation.Get("relation_type").Text());
        var from = relation.Get("issue_id").Long();
        var to = relation.Get("issue_to_id").Long() ?? 0;
        return from == issueId
            ? new SeenRelation(relation.Get("id").Long() ?? 0, type, to, relation.Get("delay"))
            : new SeenRelation(relation.Get("id").Long() ?? 0, Find(type.Reverse), from ?? 0, relation.Get("delay"));
    }

    /// <summary>"ブロック先 (blocks): #12" for issues show.</summary>
    public static string Describe(long issueId, JsonNode relation)
    {
        var seen = Seen(issueId, relation);
        var delay = seen.Delay.Long() is { } days ? $" (遅延 {days} 日)" : "";
        return $"{seen.Type.Label} ({seen.Type.Name}): #{seen.Other}{delay}";
    }

    private static List<SeenRelation> RelationsOf(JsonObject issue)
    {
        var id = issue.Get("id").Long() ?? 0;
        return (issue.Get("relations") as JsonArray ?? []).Where(r => r is not null).Select(r => Seen(id, r!)).ToList();
    }

    public static IEnumerable<Command> CreateAll() => [CreateRelations(), CreateRelate(), CreateUnrelate()];

    // ---------- relations ----------

    private static Command CreateRelations()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var command = new Command("relations", "チケットの関連の一覧 (相手の件名・ステータスつき、このチケットから見た向き)") { id }.WithNotes($$"""
            対象プロジェクト外のチケットも参照できる (その場合は stderr に注意を出す)。
            子チケットは redmine issues list --parent <id>、親子の設定は issues create / update の --parent。

            {{TypesHelp}}

            出力 (--json):
              { "issue_id", "relations": [ { "id", "relation_type", "label", "other_issue_id", "delay",
                                             "other_issue": { "id", "subject", "tracker", "status", "project" }|null } ] }
              relation_type は issue_id から見た向き (Redmine が保存している向きとは逆のこともある)。
              other_issue は相手を閲覧できないとき null。

            例:
              redmine issues relations 123
              redmine issues relations 123 --json

            終了コード: 0 成功 / 2 id が数値でない / 5 存在しない (404) など
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var issue = await ctx.GetIssueAsync(parse.GetValue(id)!, "relations");
            await IssuesCommand.WarnIfOutsideAsync(ctx, issue);
            var relations = RelationsOf(issue);
            var others = await FetchIssuesAsync(ctx, relations.Select(r => r.Other).Distinct().ToList());
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["issue_id"] = issue.Get("id")?.DeepClone(),
                    ["relations"] = new JsonArray([.. relations.Select(r => (JsonNode)new JsonObject
                    {
                        ["id"] = r.Id,
                        ["relation_type"] = r.Type.Name,
                        ["label"] = r.Type.Label,
                        ["other_issue_id"] = r.Other,
                        ["delay"] = r.Delay?.DeepClone(),
                        ["other_issue"] = others.TryGetValue(r.Other, out var other)
                            ? new JsonObject
                            {
                                ["id"] = r.Other,
                                ["subject"] = other.Get("subject")?.DeepClone(),
                                ["tracker"] = other.Get("tracker")?.DeepClone(),
                                ["status"] = other.Get("status")?.DeepClone(),
                                ["project"] = other.Get("project")?.DeepClone(),
                            }
                            : null,
                    })]),
                });
                return Exit.Ok;
            }
            if (relations.Count == 0)
            {
                Out($"#{issue.Get("id").Text()} に関連チケットはありません");
                return Exit.Ok;
            }
            var projectId = issue.Get("project").Get("id").Long();
            var columns = new List<Column<SeenRelation>>
            {
                new("関連", r => r.Type.Label),
                new("種類", r => r.Type.Name),
                new("相手", r => $"#{r.Other}", Right: true),
                new("ステータス", r => others.TryGetValue(r.Other, out var o) ? o.Get("status").Name() : ""),
                new("件名", r => others.TryGetValue(r.Other, out var o) ? o.Get("subject").Text() : "(閲覧できません)", Max: 50),
                new("遅延", r => r.Delay.Long() is { } days ? $"{days} 日" : "", Right: true),
                new("関連 id", r => r.Id.ToString(CultureInfo.InvariantCulture), Right: true),
            };
            if (others.Values.Any(o => o.Get("project").Get("id").Long() != projectId))
            {
                columns.Insert(5, new("プロジェクト", r => others.TryGetValue(r.Other, out var o) ? o.Get("project").Name() : ""));
            }
            Out($"#{issue.Get("id").Text()} {issue.Get("subject").Text()}");
            Out(Table(relations, [.. columns]));
            return Exit.Ok;
        });
        return command;
    }

    /// <summary>The issues with these ids the user can see, by id (100 per request).</summary>
    private static async Task<Dictionary<long, JsonNode>> FetchIssuesAsync(Context ctx, List<long> ids)
    {
        var found = new Dictionary<long, JsonNode>();
        foreach (var chunk in ids.Chunk(100))
        {
            var data = await ctx.Client.GetAsync("/issues.json", new Query
            {
                { "issue_id", string.Join(",", chunk) },
                { "status_id", "*" },
                { "limit", 100 },
            });
            foreach (var issue in (data.Get("issues") as JsonArray).Detach())
            {
                if (issue.Get("id").Long() is { } issueId)
                {
                    found[issueId] = issue;
                }
            }
        }
        return found;
    }

    // ---------- relate ----------

    private static Command CreateRelate()
    {
        var id = new Argument<string>("id") { Description = "関連を付けるチケット id" };
        var other = new Argument<string>("other") { Description = "相手のチケット id" };
        var type = new Option<string>("--type") { Description = "関連の種類 (<id> から見た向き。既定 relates)", HelpName = "type", DefaultValueFactory = _ => "relates" };
        var delay = new Option<string>("--delay") { Description = "precedes / follows のとき、間の日数", HelpName = "days" };
        var command = new Command("relate", "2 つのチケットに関連を付ける (どちらも対象プロジェクト内のみ)") { id, other, type, delay }.WithNotes($$"""
            {{TypesHelp}}

            安全装置:
              関連は両方のチケットの履歴に残るので、<id> と <相手> の両方が .redmine.json の project に属していなければ拒否する (終了コード 4)。
              送信前に宛先と内容を stderr に表示し、--yes が無ければ対話で確認する。--dry-run で送信内容を確認できる。

            出力 (--json): Redmine の応答 { "relation": { "id", "issue_id", "issue_to_id", "relation_type", "delay" } }
              Redmine は向きをそろえて保存することがある (blocked は相手側から見た blocks として保存される)。

            例:
              redmine issues relate 123 456                        # 関連している
              redmine issues relate 123 456 --type blocks --yes    # 123 が終わるまで 456 を終えられない
              redmine issues relate 123 456 --type precedes --delay 2 --dry-run

            終了コード: 0 追加 / 2 引数誤り / 4 対象プロジェクト外・確認できず / 5 サーバーが拒否 (既にある関連、循環など)
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var fromId = Context.ParseId(parse.GetValue(id), "id");
            var toId = Context.ParseId(parse.GetValue(other), "相手の id");
            if (fromId == toId)
            {
                throw new CliException("同じチケットどうしには関連を付けられません", Exit.Usage);
            }
            var relationType = Parse(parse.GetValue(type) ?? "relates");
            int? days = null;
            if (parse.GetValue(delay) is { } delayText)
            {
                if (relationType.Name is not ("precedes" or "follows"))
                {
                    throw new CliException("--delay は --type precedes / follows のときだけ指定できます", Exit.Usage);
                }
                days = int.TryParse(delayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw new CliException($"--delay は日数 (整数) で指定してください: {delayText}", Exit.Usage);
            }
            using var ctx = Context.Create(g, cancellation);
            var from = await ctx.GetIssueInTargetAsync(fromId.ToString(CultureInfo.InvariantCulture));
            var to = await ctx.GetIssueInTargetAsync(toId.ToString(CultureInfo.InvariantCulture));
            var relation = new JsonObject { ["issue_to_id"] = toId, ["relation_type"] = relationType.Name };
            if (days is not null)
            {
                relation["delay"] = days;
            }
            var lines = new List<string>
            {
                $"#{fromId} {from.Get("subject").Text()}",
                $"  {relationType.Label} ({relationType.Name})",
                $"#{toId} {to.Get("subject").Text()}",
            };
            if (days is not null)
            {
                lines.Add($"遅延: {days} 日");
            }
            var path = $"/issues/{fromId}/relations.json";
            var request = new JsonObject
            {
                ["method"] = "POST",
                ["path"] = path,
                ["body"] = new JsonObject { ["relation"] = relation.DeepClone() },
            };
            if (!await ctx.ConfirmWriteAsync("関連の追加", lines, request, g))
            {
                return Exit.Ok;
            }
            var res = await ctx.Client.PostAsync(path, new JsonObject { ["relation"] = relation });
            if (g.Json)
            {
                PrintJson(res);
                return Exit.Ok;
            }
            Out($"関連を付けました: #{fromId} {relationType.Label} #{toId} (関連 id {res.Get("relation").Get("id").Text()})");
            return Exit.Ok;
        });
        return command;
    }

    // ---------- unrelate ----------

    private static Command CreateUnrelate()
    {
        var id = new Argument<string>("id") { Description = "チケット id" };
        var other = new Argument<string>("other") { Description = "相手のチケット id" };
        var type = new Option<string>("--type") { Description = "外す関連の種類 (<id> から見た向き)。2 つの間に複数あるときに選ぶ", HelpName = "type" };
        var command = new Command("unrelate", "2 つのチケットの関連を外す (どちらも対象プロジェクト内のみ)") { id, other, type }.WithNotes("""
            <id> と <相手> の間の関連を探して消す。間に複数あるときは --type で選ぶ (向きは <id> から見たもの)。

            安全装置:
              関連の追加と同じく、両方のチケットが .redmine.json の project に属していなければ拒否する (終了コード 4)。
              送信前に宛先と内容を stderr に表示し、--yes が無ければ対話で確認する。--dry-run で送信内容を確認できる。

            出力 (--json): { "deleted": { "id", "relation_type", "label", "issue_id", "other_issue_id" } }

            例:
              redmine issues unrelate 123 456 --yes
              redmine issues unrelate 123 456 --type blocks --dry-run

            終了コード: 0 削除 / 2 関連が無い・選べない / 4 対象プロジェクト外・確認できず / 5 サーバーエラー
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var fromId = Context.ParseId(parse.GetValue(id), "id");
            var toId = Context.ParseId(parse.GetValue(other), "相手の id");
            var wanted = parse.GetValue(type) is { } typeText ? Parse(typeText) : null;
            using var ctx = Context.Create(g, cancellation);
            var from = await ctx.GetIssueInTargetAsync(fromId.ToString(CultureInfo.InvariantCulture), "relations");
            var all = RelationsOf(from);
            var between = all.Where(r => r.Other == toId && (wanted is null || r.Type.Name == wanted.Name)).ToList();
            string Listing(IEnumerable<SeenRelation> relations) =>
                string.Join(", ", relations.Select(r => $"{r.Type.Name} #{r.Other}")) is { Length: > 0 } text ? text : "(なし)";
            if (between.Count == 0)
            {
                throw new CliException(
                    wanted is null ? $"#{fromId} と #{toId} の間に関連はありません" : $"#{fromId} と #{toId} の間に {wanted.Name} の関連はありません",
                    Exit.Usage,
                    $"#{fromId} の関連: {Listing(all)}");
            }
            if (between.Count > 1)
            {
                throw new CliException($"#{fromId} と #{toId} の間に関連が {between.Count} 件あります。--type で選んでください", Exit.Usage,
                    $"候補: {Listing(between)}");
            }
            var target = between[0];
            var to = await ctx.GetIssueInTargetAsync(toId.ToString(CultureInfo.InvariantCulture));
            var lines = new List<string>
            {
                $"#{fromId} {from.Get("subject").Text()}",
                $"  {target.Type.Label} ({target.Type.Name})  ← この関連を外す (関連 id {target.Id})",
                $"#{toId} {to.Get("subject").Text()}",
            };
            var path = $"/relations/{target.Id}.json";
            var request = new JsonObject { ["method"] = "DELETE", ["path"] = path };
            if (!await ctx.ConfirmWriteAsync("関連の削除", lines, request, g))
            {
                return Exit.Ok;
            }
            await ctx.Client.RequestAsync("DELETE", path);
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["deleted"] = new JsonObject
                    {
                        ["id"] = target.Id,
                        ["relation_type"] = target.Type.Name,
                        ["label"] = target.Type.Label,
                        ["issue_id"] = fromId,
                        ["other_issue_id"] = toId,
                    },
                });
                return Exit.Ok;
            }
            Out($"関連を外しました: #{fromId} {target.Type.Label} #{toId}");
            return Exit.Ok;
        });
        return command;
    }
}
