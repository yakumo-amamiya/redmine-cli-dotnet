using System.CommandLine;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine fields: the custom fields usable in the target project's issues.</summary>
internal static class FieldsCommand
{
    private static readonly Dictionary<string, string> FormatLabels = new()
    {
        ["string"] = "テキスト",
        ["text"] = "長文",
        ["int"] = "整数",
        ["float"] = "数値",
        ["date"] = "日付",
        ["bool"] = "真偽",
        ["list"] = "リスト",
        ["enumeration"] = "キーバリューリスト",
        ["user"] = "ユーザー",
        ["version"] = "バージョン",
        ["link"] = "リンク",
        ["attachment"] = "ファイル",
    };

    private sealed record Observed(List<string> Values, bool Multiple);

    private sealed record Row(CustomField Field, Observed? Observed);

    public static Command Create()
    {
        var sample = new Option<int>("--sample")
        {
            Description = "既存チケットを新しい順に n 件読み、実際に入っている値を「観測値」として示す (0 で省略)",
            HelpName = "n",
            DefaultValueFactory = _ => 100,
        };
        var command = new Command("fields", "対象プロジェクトのチケットで使えるカスタムフィールドの一覧 (名前、型、必須、複数、選択肢)") { sample }.WithNotes("""
            --field "名前=値" で指定する前に、名前と選べる値を確認する。

            正規の情報源と代替:
              型・必須・複数・選択肢 (定義) は管理者権限の API (/custom_fields.json) から取る。これが正規の情報源で、
              取れれば --field の値を型に合わせて変換・検証できる。
              権限が無い環境では id と名前しか取れないので、代わりに既存チケットに実際に入っている値を --sample 件ぶん集めて
              「観測値」として示す。観測値は定義ではない: まだ使われていない選択肢は出ず、型も分からない。
              表記を確かめる手がかりとしてだけ使い、--field の値の検証はサーバー側 (422) に任せる。
              定義が取れた場合は既定では観測しない (--sample を明示すれば併記する)。

            出力 (--json):
              { "fields": [ { "id", "name", "format", "multiple", "required", "possible_values": [ {"value","label"?} ]|null,
                              "trackers": [...], "detailed": bool,
                              "observed_values": [...]|null, "observed_multiple": bool|null } ],
                "sampled_issues": n|null }

            例:
              redmine fields
              redmine fields --json
              redmine fields --sample 300     # 観測する件数を増やす
              redmine fields --sample 0       # 観測しない
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var fields = await ctx.Lookup.CustomFields();
            var detailed = fields.Any(f => f.Detailed);
            var sampleExplicit = parse.GetResult(sample) is { Implicit: false };
            var sampleLimit = Math.Max(0, parse.GetValue(sample));
            int? sampled = null;
            List<Row> rows;
            if (fields.Count > 0 && sampleLimit > 0 && (!detailed || sampleExplicit))
            {
                var (count, observed) = await SampleValuesAsync(ctx, fields, sampleLimit);
                sampled = count;
                rows = fields.Select(f => new Row(f, observed[f.Id])).ToList();
            }
            else
            {
                rows = fields.Select(f => new Row(f, null)).ToList();
            }
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["fields"] = new JsonArray([.. rows.Select(r =>
                    {
                        var json = r.Field.ToJson();
                        json["observed_values"] = r.Observed is null ? null : new JsonArray([.. r.Observed.Values.Select(v => (JsonNode)v)]);
                        json["observed_multiple"] = r.Observed?.Multiple;
                        return (JsonNode)json;
                    })]),
                    ["sampled_issues"] = sampled,
                });
                return Exit.Ok;
            }
            if (fields.Count == 0)
            {
                Out("対象プロジェクトで使えるカスタムフィールドはありません");
                return Exit.Ok;
            }
            var columns = new List<Column<Row>>
            {
                new("id", r => r.Field.Id.ToString(), Right: true),
                new("名前", r => r.Field.Name),
            };
            if (detailed)
            {
                columns.AddRange(
                [
                    new("型", r => r.Field.Format is { } f ? FormatLabels.GetValueOrDefault(f, f) : "?"),
                    new("必須", r => r.Field.Required == true ? "必須" : ""),
                    new("複数", r => r.Field.Multiple == true ? "複数" : ""),
                    new("選択肢", r => r.Field.PossibleValues is { } values
                        ? string.Join(" | ", values.Select(o => (o.Get("label") ?? o.Get("value")).Text()))
                        : "", Max: 60),
                    new("トラッカー", r => string.Join(", ", r.Field.Trackers), Max: 30),
                ]);
            }
            if (sampled is not null)
            {
                columns.AddRange(
                [
                    new("観測: 複数", r => r.Observed?.Multiple == true ? "複数" : ""),
                    new($"観測値 ({sampled} 件から)", r => string.Join(" | ", r.Observed?.Values ?? []), Max: 60),
                ]);
            }
            Out(Table(rows, [.. columns]));
            if (!detailed)
            {
                Info("注意: 型と選択肢 (定義) は管理者権限が無いため取得できませんでした。");
                Info("      観測値は既存チケットの値で、定義ではありません (未使用の選択肢は出ず、型も分かりません)。--field の値はサーバー側で検証されます。");
            }
            return Exit.Ok;
        });
        return command;
    }

    /// <summary>
    /// The values actually found in existing issues: a stand-in for the choices when the definitions need admin rights.
    /// </summary>
    private static async Task<(int Sampled, Dictionary<long, Observed> ByField)> SampleValuesAsync(Context ctx, List<CustomField> fields, int limit)
    {
        var p = await ctx.TargetProjectAsync();
        var (items, _) = await ctx.Client.ListAllAsync(
            "/issues.json",
            "issues",
            new Query { { "project_id", p.Id }, { "status_id", "*" }, { "sort", "updated_on:desc" } },
            max: limit);
        var seen = fields.ToDictionary(f => f.Id, _ => (Values: new SortedSet<string>(StringComparer.Ordinal), Multiple: false));
        foreach (var issue in items)
        {
            foreach (var cf in (issue.Get("custom_fields") as JsonArray ?? []).OfType<JsonObject>())
            {
                if (cf.Get("id").Long() is not { } id || !seen.TryGetValue(id, out var entry))
                {
                    continue;
                }
                var value = cf.Get("value");
                if (value is JsonArray array)
                {
                    entry.Multiple = true;
                    foreach (var v in array)
                    {
                        if (v.Text() is { Length: > 0 } text)
                        {
                            entry.Values.Add(text);
                        }
                    }
                }
                else if (value.Text() is { Length: > 0 } text)
                {
                    entry.Values.Add(text);
                }
                seen[id] = entry;
            }
        }
        return (items.Count, seen.ToDictionary(pair => pair.Key, pair => new Observed([.. pair.Value.Values], pair.Value.Multiple)));
    }
}
