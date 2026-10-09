using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine time: logging spent time and listing it.</summary>
internal static class TimeCommand
{
    public static Command Create()
    {
        var time = new Command("time", "作業時間の記録と一覧");
        time.Subcommands.Add(CreateLog());
        time.Subcommands.Add(CreateList());
        return time;
    }

    private static string? CheckDate(string? value, string label) => value is null ? null : Lookups.CheckDate(value, label);

    private static Command CreateLog()
    {
        var issueArg = new Argument<string>("issue") { Description = "チケット id (123 か #123)" };
        var hours = new Option<string>("--hours") { Description = "時間 (例: 1.5)", HelpName = "hours", Required = true };
        var activity = new Option<string>("--activity") { Description = "作業分類名 | id (省略時は Redmine の既定の作業分類)", HelpName = "activity" };
        var comment = new Option<string>("--comment") { Description = "コメント", HelpName = "text" };
        var date = new Option<string>("--date") { Description = "作業日 YYYY-MM-DD (省略時は今日)", HelpName = "date" };
        var command = new Command("log", "チケットに作業時間を記録する (対象プロジェクト内のみ)") { issueArg, hours, activity, comment, date }.WithNotes("""
            安全装置: チケットの所属プロジェクトを確認してから送る。宛先表示と --yes / 対話確認 / --dry-run は他の書き込みと同じ。

            出力 (--json): { "time_entry": { "id", "issue": {"id"}, "hours", "activity", "comments", "spent_on", "user", ... } }

            例:
              redmine time log 123 --hours 1.5 --comment "調査" --yes
              redmine time log 123 --hours 2 --activity 開発 --date 2026-09-05 --yes
              redmine time log 123 --hours 0.5 --dry-run

            終了コード: 0 記録 / 2 引数誤り・作業分類が不明 / 4 対象プロジェクト外・確認できず / 5 サーバーが拒否
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var hoursText = parse.GetValue(hours)!;
            if (!double.TryParse(hoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var spent) || !double.IsFinite(spent) || spent <= 0)
            {
                throw new CliException($"--hours は正の数で指定してください: {hoursText}", Exit.Usage);
            }
            using var ctx = Context.Create(g, cancellation);
            var issue = await ctx.GetIssueInTargetAsync(parse.GetValue(issueArg)!);
            var activities = await ctx.Lookup.Activities();
            long activityId;
            if (parse.GetValue(activity) is { } activityText)
            {
                activityId = await ctx.Lookup.ResolveActivity(activityText);
            }
            else
            {
                var fallback = activities.FirstOrDefault(a => a.Raw.Get("is_default").Truthy()) ?? throw new CliException(
                    "既定の作業分類が無いので --activity を指定してください",
                    Exit.Usage,
                    $"選べる作業分類: {(activities.Count == 0 ? "(なし)" : string.Join(", ", activities.Select(a => $"{a.Name} ({a.Id})")))}");
                activityId = fallback.Id;
            }
            var activityName = activities.FirstOrDefault(a => a.Id == activityId)?.Name ?? activityId.ToString(CultureInfo.InvariantCulture);
            var entry = new JsonObject { ["issue_id"] = issue.Get("id")?.DeepClone(), ["hours"] = spent, ["activity_id"] = activityId };
            var commentText = parse.GetValue(comment);
            if (commentText is not null)
            {
                entry["comments"] = commentText;
            }
            var day = CheckDate(parse.GetValue(date), "--date");
            if (day is not null)
            {
                entry["spent_on"] = day;
            }
            var lines = new List<string>
            {
                $"#{issue.Get("id").Text()} {issue.Get("subject").Text()}",
                $"時間: {spent.ToString(CultureInfo.InvariantCulture)}h",
                $"作業分類: {activityName}",
                $"作業日: {day ?? "今日"}",
            };
            if (commentText is not null)
            {
                lines.Add($"コメント: {commentText}");
            }
            var request = new JsonObject
            {
                ["method"] = "POST",
                ["path"] = "/time_entries.json",
                ["body"] = new JsonObject { ["time_entry"] = entry.DeepClone() },
            };
            if (!await ctx.ConfirmWriteAsync("作業時間の記録", lines, request, g))
            {
                return Exit.Ok;
            }
            var res = await ctx.Client.PostAsync("/time_entries.json", new JsonObject { ["time_entry"] = entry });
            if (g.Json)
            {
                PrintJson(res);
                return Exit.Ok;
            }
            var t = RedmineClient.Expect(res, "time_entry");
            Out($"記録しました: #{issue.Get("id").Text()} に {t.Get("hours").Text()}h ({t.Get("activity").Name()}, {t.Get("spent_on").Text()}, entry id {t.Get("id").Text()})");
            return Exit.Ok;
        });
        return command;
    }

    private static Command CreateList()
    {
        var issue = new Option<string>("--issue") { Description = "チケット id で絞る", HelpName = "id" };
        var user = new Option<string>("--user") { Description = "me | ユーザー id | メンバー名 | all (全員)", HelpName = "who", DefaultValueFactory = _ => "me" };
        var from = new Option<string>("--from") { Description = "開始日 YYYY-MM-DD", HelpName = "date" };
        var to = new Option<string>("--to") { Description = "終了日 YYYY-MM-DD", HelpName = "date" };
        var all = new Option<bool>("--all") { Description = "全プロジェクトを対象にする (読み取りのみ)" };
        var limit = new Option<int>("--limit") { Description = "最大件数 (上限 100)", HelpName = "n", DefaultValueFactory = _ => 50 };
        var offset = new Option<int>("--offset") { Description = "開始位置", HelpName = "n", DefaultValueFactory = _ => 0 };
        var command = new Command("list", "作業時間の一覧。既定は対象プロジェクトの自分の記録 (新しい順)") { issue, user, from, to, all, limit, offset }.WithNotes("""
            出力 (--json): { "time_entries": [ { "id", "project", "issue": {"id"}, "user", "activity", "hours", "comments", "spent_on" } ],
                             "total_count", "offset", "limit" }

            例:
              redmine time list
              redmine time list --from 2026-09-01 --to 2026-09-30
              redmine time list --issue 123 --user all
              redmine time list --all --json
            """);
        command.Aliases.Add("ls");
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var allProjects = parse.GetValue(all);
            using var ctx = Context.Create(g, cancellation);
            var query = new Query();
            if (!allProjects)
            {
                query.Set("project_id", ctx.Config.Project);
            }
            if (parse.GetValue(issue) is { } issueText)
            {
                query.Set("issue_id", Context.ParseId(issueText, "--issue"));
            }
            var who = parse.GetValue(user) ?? "me";
            if (who != "all")
            {
                query.Set("user_id", who == "me" ? (await ctx.CurrentUserAsync()).Get("id").Long() : await ctx.Lookup.ResolveAssignee(who));
            }
            if (CheckDate(parse.GetValue(from), "--from") is { Length: > 0 } fromDate)
            {
                query.Set("from", fromDate);
            }
            if (CheckDate(parse.GetValue(to), "--to") is { Length: > 0 } toDate)
            {
                query.Set("to", toDate);
            }
            query.Set("limit", parse.GetValue(limit) is > 0 and var n ? Math.Min(n, 100) : 50);
            query.Set("offset", Math.Max(0, parse.GetValue(offset)));
            var data = await ctx.Client.GetAsync("/time_entries.json", query);
            if (g.Json)
            {
                PrintJson(data);
                return Exit.Ok;
            }
            var rows = (data.Get("time_entries") as JsonArray ?? []).Where(x => x is not null).Select(x => x!).ToList();
            if (rows.Count == 0)
            {
                Out("該当する記録はありません");
                return Exit.Ok;
            }
            var columns = new List<Column<JsonNode>>
            {
                new("id", t => t.Get("id").Text(), Right: true),
                new("作業日", t => t.Get("spent_on").Text()),
                new("チケット", t => t.Get("issue") is { } i ? $"#{i.Get("id").Text()}" : "", Right: true),
                new("時間", t => t.Get("hours").Text(), Right: true),
                new("作業分類", t => t.Get("activity").Name()),
                new("ユーザー", t => t.Get("user").Name()),
                new("コメント", t => t.Get("comments").Text(), Max: 50),
            };
            if (allProjects)
            {
                columns.Add(new("プロジェクト", t => t.Get("project").Name()));
            }
            Out(Table(rows, [.. columns]));
            var sum = rows.Sum(t => t.Get("hours").Double() ?? 0);
            var total = data.Get("total_count").Long() ?? rows.Count;
            var next = (data.Get("offset").Long() ?? 0) + rows.Count;
            Out($"({rows.Count} 件 合計 {sum.ToString("F2", CultureInfo.InvariantCulture)}h / 該当 {total} 件{(next < total ? $"。続きは --offset {next}" : "")})");
            return Exit.Ok;
        });
        return command;
    }
}
