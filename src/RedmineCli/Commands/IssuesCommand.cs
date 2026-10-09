using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using static RedmineCli.Output;

namespace RedmineCli.Commands;

/// <summary>redmine issues: list, show, create, update, comment, files, attach, download.</summary>
internal static class IssuesCommand
{
    private const string FieldOptionsHelp = """
        値の指定 (--tracker などは名前でも id でもよい。名前は大文字小文字を区別せず、一意な部分一致も可):
          --assignee   me | ユーザー id | メンバー名 | none (未割当にする)
          --status     ステータス名 | id     (例: "New", "進行中", 2)
          --parent     チケット id | none    (親も対象プロジェクト内に限る)
          --category / --version   名前 | id | none
          --field      "名前=値" でカスタムフィールド。複数回指定可。同じ名前を繰り返すと複数選択。"名前=" でクリア。
                       使えるフィールドと選択肢は `redmine fields` で確認。真偽は yes/no、リストは選択肢の表示名、
                       ユーザー型は me | id | メンバー名、日付は YYYY-MM-DD
          日付は YYYY-MM-DD。--estimated は時間 (1.5 など)。--done は 0〜100。
          長文は --description-file <ファイル> (- で stdin) で渡す。
        """;

    private static readonly Dictionary<string, string> FieldLabels = new()
    {
        ["subject"] = "件名",
        ["description"] = "説明",
        ["tracker_id"] = "トラッカー id",
        ["status_id"] = "ステータス id",
        ["priority_id"] = "優先度 id",
        ["assigned_to_id"] = "担当者 id",
        ["category_id"] = "カテゴリ id",
        ["fixed_version_id"] = "対象バージョン id",
        ["parent_issue_id"] = "親チケット",
        ["start_date"] = "開始日",
        ["due_date"] = "期日",
        ["estimated_hours"] = "予定工数",
        ["done_ratio"] = "進捗率",
        ["is_private"] = "プライベート",
        ["notes"] = "コメント",
        ["private_notes"] = "プライベートコメント",
    };

    public static Command Create()
    {
        var issues = new Command("issues", "チケットの一覧・参照・作成・更新・コメント・添付・関連").WithNotes("""
            読み取り (どのプロジェクトでも可): list, show, files, download, relations
            書き込み (.redmine.json の project 内のみ): create, update, comment, attach, relate, unrelate

            親子: 親は create / update の --parent <id> (none で外す)。子の一覧は list --parent <id>
            関連: relations で一覧、relate で付ける、unrelate で外す

            例:
              redmine issues list --mine
              redmine issues show 123
              redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --assignee me --yes
              redmine issues update 123 --status "進行中" --done 30 --note "着手" --yes
              redmine issues update 456 --parent 123 --yes          # 456 を 123 の子にする
              redmine issues list --parent 123 --status all         # 123 の子チケット
              redmine issues relate 123 456 --type blocks --yes     # 123 が終わるまで 456 を終えられない
              redmine issues attach 123 ./screenshot.png --note "再現時の画面" --yes
              redmine issues download 123 --all --dir ./tmp
            """);
        issues.Aliases.Add("issue");
        issues.Subcommands.Add(CreateList());
        issues.Subcommands.Add(CreateShow());
        issues.Subcommands.Add(CreateCreate());
        issues.Subcommands.Add(CreateUpdate());
        issues.Subcommands.Add(CreateComment());
        issues.Subcommands.Add(CreateFiles());
        issues.Subcommands.Add(CreateAttach());
        issues.Subcommands.Add(CreateDownload());
        foreach (var command in RelationsCommand.CreateAll())
        {
            issues.Subcommands.Add(command);
        }
        return issues;
    }

    private static Option<string> Text(string name, string description, string helpName) => new(name) { Description = description, HelpName = helpName };

    private static Option<string[]> Repeated(string name, string description, string helpName) =>
        new(name) { Description = description, HelpName = helpName, DefaultValueFactory = _ => [] };

    // ---------- shared ----------

    /// <summary>The issue fields create and update share. Only what was given goes into the request.</summary>
    private sealed record FieldValues(
        string? Subject, string? Description, string? DescriptionFile, string? Tracker, string? Status, string? Priority, string? Assignee,
        string? Category, string? Version, string? Parent, string? StartDate, string? DueDate, string? Estimated, string? Done,
        bool? Private, string[] Field)
    {
        public static readonly FieldValues None = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, []);
    }

    private sealed class FieldOptions
    {
        private readonly Option<string> _subject;
        private readonly Option<string> _description;
        private readonly Option<string> _descriptionFile;
        private readonly Option<string> _tracker;
        private readonly Option<string> _status;
        private readonly Option<string> _priority;
        private readonly Option<string> _assignee;
        private readonly Option<string> _category;
        private readonly Option<string> _version;
        private readonly Option<string> _parent;
        private readonly Option<string> _startDate;
        private readonly Option<string> _dueDate;
        private readonly Option<string> _estimated;
        private readonly Option<string> _done;
        private readonly Option<bool> _private;
        private readonly Option<bool>? _noPrivate;
        private readonly Option<string[]> _field;

        public FieldOptions(Command command, bool update)
        {
            _subject = Text("--subject", update ? "件名" : "件名 (必須)", "text");
            _subject.Required = !update;
            _description = Text("--description", update ? "説明 (全文を置き換える)" : "説明", "text");
            _descriptionFile = Text("--description-file", "説明をファイルから読む (- で stdin)", "file");
            _tracker = Text("--tracker", update ? "トラッカー名 | id" : "トラッカー名 | id (省略時はプロジェクトの既定)", "tracker");
            _status = Text("--status", "ステータス名 | id", "status");
            _priority = Text("--priority", "優先度名 | id", "priority");
            _assignee = Text("--assignee", update ? "me | ユーザー id | メンバー名 | none" : "me | ユーザー id | メンバー名", "who");
            _category = Text("--category", update ? "カテゴリ名 | id | none" : "カテゴリ名 | id", "category");
            _version = Text("--version", update ? "対象バージョン名 | id | none" : "対象バージョン名 | id", "version");
            _parent = Text("--parent", update ? "親チケット id | none" : "親チケット id (対象プロジェクト内)", "id");
            _startDate = Text("--start-date", update ? "開始日 YYYY-MM-DD | none" : "開始日 YYYY-MM-DD", "date");
            _dueDate = Text("--due-date", update ? "期日 YYYY-MM-DD | none" : "期日 YYYY-MM-DD", "date");
            _estimated = Text("--estimated", "予定工数 (時間)", "hours");
            _done = Text("--done", "進捗率 0〜100", "percent");
            _private = new Option<bool>("--private") { Description = update ? "プライベートにする" : "プライベートチケットにする" };
            _noPrivate = update ? new Option<bool>("--no-private") { Description = "プライベートを解除する" } : null;
            _field = Repeated("--field", update ? "カスタムフィールド (複数回指定可。\"名前=\" でクリア)" : "カスタムフィールド (複数回指定可)", "name=value");
            Option[] options =
            [
                _subject, _description, _descriptionFile, _tracker, _status, _priority, _assignee, _category, _version, _parent,
                _startDate, _dueDate, _estimated, _done, _private,
            ];
            foreach (var option in options)
            {
                command.Options.Add(option);
            }
            if (_noPrivate is not null)
            {
                command.Options.Add(_noPrivate);
            }
            command.Options.Add(_field);
        }

        public FieldValues Read(ParseResult parse)
        {
            var on = parse.GetValue(_private);
            var off = _noPrivate is not null && parse.GetValue(_noPrivate);
            if (on && off)
            {
                throw new CliException("--private と --no-private は同時に指定できません", Exit.Usage);
            }
            return new FieldValues(
                parse.GetValue(_subject), parse.GetValue(_description), parse.GetValue(_descriptionFile), parse.GetValue(_tracker),
                parse.GetValue(_status), parse.GetValue(_priority), parse.GetValue(_assignee), parse.GetValue(_category),
                parse.GetValue(_version), parse.GetValue(_parent), parse.GetValue(_startDate), parse.GetValue(_dueDate),
                parse.GetValue(_estimated), parse.GetValue(_done), on ? true : off ? false : null, parse.GetValue(_field) ?? []);
        }
    }

    private static string? CheckDateOrNone(string? value, string label)
    {
        if (value is null)
        {
            return null;
        }
        return value is "none" or "" ? "" : Lookups.CheckDate(value, label);
    }

    private static string IssueUrl(Context ctx, JsonNode? id) => $"{ctx.Config.Url}/issues/{id.Text()}";

    /// <summary>The issue fields for create and update, with the custom fields' names kept for the confirmation (not sent).</summary>
    private static async Task<(JsonObject Fields, List<ResolvedCustomField>? Custom)> BuildIssueFieldsAsync(Context ctx, FieldValues v)
    {
        var f = new JsonObject();
        if (v.Subject is not null)
        {
            f["subject"] = v.Subject;
        }
        if (Input.ReadTextOption(v.Description, v.DescriptionFile, "description") is { } description)
        {
            f["description"] = description;
        }
        if (v.Tracker is not null)
        {
            f["tracker_id"] = await ctx.Lookup.ResolveTracker(v.Tracker);
        }
        if (v.Status is not null)
        {
            f["status_id"] = await ctx.Lookup.ResolveStatus(v.Status);
        }
        if (v.Priority is not null)
        {
            f["priority_id"] = await ctx.Lookup.ResolvePriority(v.Priority);
        }
        if (v.Assignee is not null)
        {
            f["assigned_to_id"] = v.Assignee == "none" ? "" : await ctx.Lookup.ResolveAssignee(v.Assignee);
        }
        if (v.Category is not null)
        {
            f["category_id"] = v.Category == "none" ? "" : await ctx.Lookup.ResolveCategory(v.Category);
        }
        if (v.Version is not null)
        {
            f["fixed_version_id"] = v.Version == "none" ? "" : await ctx.Lookup.ResolveVersion(v.Version);
        }
        if (v.Parent is not null)
        {
            if (v.Parent == "none")
            {
                f["parent_issue_id"] = "";
            }
            else
            {
                // The parent too must be in the target project (no parent-child links across projects).
                var parent = await ctx.GetIssueInTargetAsync(v.Parent);
                f["parent_issue_id"] = parent.Get("id").Long();
            }
        }
        if (CheckDateOrNone(v.StartDate, "--start-date") is { } start)
        {
            f["start_date"] = start;
        }
        if (CheckDateOrNone(v.DueDate, "--due-date") is { } due)
        {
            f["due_date"] = due;
        }
        if (v.Estimated is not null)
        {
            if (!double.TryParse(v.Estimated, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || !double.IsFinite(hours) || hours < 0)
            {
                throw new CliException($"--estimated は時間数 (例: 1.5): {v.Estimated}", Exit.Usage);
            }
            f["estimated_hours"] = hours;
        }
        if (v.Done is not null)
        {
            if (!double.TryParse(v.Done, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) || ratio != Math.Floor(ratio) || ratio is < 0 or > 100)
            {
                throw new CliException($"--done は 0〜100 の整数: {v.Done}", Exit.Usage);
            }
            f["done_ratio"] = (int)ratio;
        }
        if (v.Private is { } isPrivate)
        {
            f["is_private"] = isPrivate;
        }
        List<ResolvedCustomField>? custom = null;
        if (v.Field.Length > 0)
        {
            custom = await ctx.Lookup.ResolveCustomFields(v.Field);
            f["custom_fields"] = new JsonArray([.. custom.Select(cf => (JsonNode)new JsonObject { ["id"] = cf.Id, ["value"] = cf.Value.DeepClone() })]);
        }
        return (f, custom);
    }

    private static string ShowCustomValue(JsonNode? value) => value switch
    {
        JsonArray array => $"[{string.Join(", ", array.Select(v => v.Text()))}]",
        _ when value.Str() == "" => "(クリア)",
        _ => value.Text(),
    };

    private static List<string> DescribeFields(JsonObject fields, List<ResolvedCustomField>? custom)
    {
        var lines = new List<string>();
        foreach (var (key, value) in fields)
        {
            if (key == "custom_fields")
            {
                var shown = custom is not null
                    ? custom.Select(cf => $"カスタム「{cf.Name}」(id {cf.Id}): {ShowCustomValue(cf.Value)}")
                    : (value as JsonArray ?? []).Select(cf => $"カスタム「{cf.Get("id").Text()}」(id {cf.Get("id").Text()}): {ShowCustomValue(cf.Get("value"))}");
                lines.Add(string.Join("\n  ", shown));
                continue;
            }
            var label = FieldLabels.GetValueOrDefault(key, key);
            var text = key is "description" or "notes" ? Summarize(value.Text()) : value.Str() == "" ? "(クリア)" : value.Text();
            lines.Add($"{label}: {text}");
        }
        return lines;
    }

    private static string Summarize(string? text)
    {
        var lines = (text ?? "").Split('\n');
        var first = lines[0];
        return lines.Length > 1 ? $"{Cut(first, 60)} … ({lines.Length} 行)" : Cut(first, 80);
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    // ---------- display ----------

    private static string DescribeDetail(JsonNode? d, Dictionary<string, Dictionary<string, string>> names)
    {
        var property = d.Get("property").Text();
        var name = d.Get("name").Text();
        var label = property == "attr" ? name : $"{property}:{name}";
        names.TryGetValue(name, out var map);
        string Show(JsonNode? v) =>
            map is not null && v is not null && map.TryGetValue(v.Text(), out var shown) ? $"{shown} ({v.Text()})" : v.Text();
        var oldValue = d.Get("old_value");
        var newValue = d.Get("new_value");
        if (oldValue is null || oldValue.Str() == "")
        {
            return $"{label} = {Show(newValue)}";
        }
        if (newValue is null || newValue.Str() == "")
        {
            return $"{label}: {Show(oldValue)} を削除";
        }
        return $"{label}: {Show(oldValue)} → {Show(newValue)}";
    }

    /// <summary>Tables to show the journal's status_id and so on by name. Lists that cannot be read stay ids.</summary>
    private static async Task<Dictionary<string, Dictionary<string, string>>> JournalNameMapsAsync(Context ctx, JsonObject issue)
    {
        var attrs = new HashSet<string>();
        foreach (var j in issue.Get("journals") as JsonArray ?? [])
        {
            foreach (var d in j.Get("details") as JsonArray ?? [])
            {
                if (d.Get("property").Str() == "attr")
                {
                    attrs.Add(d.Get("name").Text());
                }
            }
        }
        var maps = new Dictionary<string, Dictionary<string, string>>();
        async Task Load(string attr, Func<Task<List<Named>>> loader)
        {
            if (!attrs.Contains(attr))
            {
                return;
            }
            try
            {
                maps[attr] = (await loader()).GroupBy(x => x.Id).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.First().Name);
            }
            catch (CliException)
            {
                // No right to read the list: the ids stay.
            }
        }
        await Load("status_id", ctx.Lookup.Statuses);
        await Load("tracker_id", ctx.Lookup.Trackers);
        await Load("priority_id", ctx.Lookup.Priorities);
        await Load("assigned_to_id", ctx.Lookup.Members);
        return maps;
    }

    private static string AttachmentTable(IReadOnlyList<JsonNode> attachments) => Table(attachments,
        new Column<JsonNode>("id", a => a.Get("id").Text(), Right: true),
        new Column<JsonNode>("ファイル名", a => a.Get("filename").Text()),
        new Column<JsonNode>("サイズ", a => FormatSize(a.Get("filesize")), Right: true),
        new Column<JsonNode>("作成者", a => a.Get("author").Name()),
        new Column<JsonNode>("日時", a => ShortDate(a.Get("created_on"))),
        new Column<JsonNode>("説明", a => a.Get("description").Text(), Max: 40));

    private static List<JsonNode> Items(JsonNode? array) => (array as JsonArray ?? []).Where(x => x is not null).Select(x => x!).ToList();

    private static async Task<string> RenderIssueAsync(Context ctx, JsonObject issue, bool journals)
    {
        var lines = new List<string>
        {
            $"#{issue.Get("id").Text()} {issue.Get("subject").Text()}",
            IssueUrl(ctx, issue.Get("id")),
            $"プロジェクト: {issue.Get("project").Name()} | トラッカー: {issue.Get("tracker").Name()} | ステータス: {issue.Get("status").Name()} | 優先度: {issue.Get("priority").Name()}",
            $"担当: {(issue.Get("assigned_to").Name() is { Length: > 0 } assignee ? assignee : "(未割当)")} | 作成者: {issue.Get("author").Name()} | 作成: {ShortDate(issue.Get("created_on"))} | 更新: {ShortDate(issue.Get("updated_on"))}",
        };
        var extras = new List<string> { $"進捗: {(issue.Get("done_ratio") is { } done ? done.Text() : "0")}%" };
        if (issue.Get("start_date").Truthy())
        {
            extras.Add($"開始: {issue.Get("start_date").Text()}");
        }
        if (issue.Get("due_date").Truthy())
        {
            extras.Add($"期日: {issue.Get("due_date").Text()}");
        }
        if (issue.Get("estimated_hours") is { } estimated)
        {
            extras.Add($"予定工数: {estimated.Text()}h");
        }
        if (issue.Get("spent_hours") is { } spent)
        {
            extras.Add($"実績: {spent.Text()}h");
        }
        if (issue.Get("category").Truthy())
        {
            extras.Add($"カテゴリ: {issue.Get("category").Name()}");
        }
        if (issue.Get("fixed_version").Truthy())
        {
            extras.Add($"バージョン: {issue.Get("fixed_version").Name()}");
        }
        if (issue.Get("parent").Truthy())
        {
            extras.Add($"親: #{issue.Get("parent").Get("id").Text()}");
        }
        if (issue.Get("is_private").Truthy())
        {
            extras.Add("プライベート");
        }
        lines.Add(string.Join(" | ", extras));
        var customFields = Items(issue.Get("custom_fields"));
        if (customFields.Count > 0)
        {
            lines.Add("--- カスタムフィールド ---");
            foreach (var cf in customFields)
            {
                var value = cf.Get("value");
                lines.Add($"{cf.Name()}: {(value is JsonArray array ? string.Join(", ", array.Select(v => v.Text())) : value.Text())}");
            }
        }
        lines.Add("--- 説明 ---");
        lines.Add(issue.Get("description").Str()?.Trim() is { Length: > 0 } description ? description : "(なし)");
        var children = Items(issue.Get("children"));
        if (children.Count > 0)
        {
            lines.Add($"--- 子チケット ({children.Count}) ---");
            lines.AddRange(children.Select(c => $"#{c.Get("id").Text()} [{c.Get("tracker").Name()}] {c.Get("subject").Text()}"));
        }
        var relations = Items(issue.Get("relations"));
        if (relations.Count > 0)
        {
            lines.Add($"--- 関連 ({relations.Count}。相手の件名は redmine issues relations {issue.Get("id").Text()}) ---");
            lines.AddRange(relations.Select(r => RelationsCommand.Describe(issue.Get("id").Long() ?? 0, r)));
        }
        var attachments = Items(issue.Get("attachments"));
        if (attachments.Count > 0)
        {
            lines.Add($"--- 添付 ({attachments.Count}) ---");
            lines.Add(AttachmentTable(attachments));
        }
        var history = Items(issue.Get("journals"));
        if (journals && history.Count > 0)
        {
            var names = await JournalNameMapsAsync(ctx, issue);
            lines.Add($"--- 履歴 ({history.Count}) ---");
            for (var i = 0; i < history.Count; i++)
            {
                var j = history[i];
                lines.Add($"[{i + 1}] {j.Get("user").Name()} {ShortDate(j.Get("created_on"))}{(j.Get("private_notes").Truthy() ? " (プライベート)" : "")}");
                lines.AddRange(Items(j.Get("details")).Select(d => $"  {DescribeDetail(d, names)}"));
                if (j.Get("notes").Str()?.Trim() is { Length: > 0 } notes)
                {
                    lines.AddRange(notes.Split('\n').Select(l => $"  {l}"));
                }
            }
        }
        return string.Join("\n", lines);
    }

    internal static async Task<bool> WarnIfOutsideAsync(Context ctx, JsonObject issue)
    {
        var target = await ctx.TargetProjectAsync();
        var inside = issue.Get("project").Get("id").Long() == target.Id;
        if (!inside)
        {
            Info($"注意: #{issue.Get("id").Text()} は対象プロジェクト外 ({issue.Get("project").Name()}) です。参照はできますが書き込みはできません。");
        }
        return inside;
    }

    // ---------- list ----------

    private static Command CreateList()
    {
        var all = new Option<bool>("--all") { Description = "全プロジェクトを対象にする (読み取りのみ)" };
        var project = Text("--project", "別プロジェクトを見る (読み取りのみ。書き込み先は変わらない)", "identifier");
        var status = new Option<string>("--status") { Description = "open | closed | all | ステータス名 | id", HelpName = "status", DefaultValueFactory = _ => "open" };
        var assignee = Text("--assignee", "me | ユーザー id | メンバー名 | none (未割当)", "who");
        var mine = new Option<bool>("--mine") { Description = "--assignee me と同じ" };
        var tracker = Text("--tracker", "トラッカー名 | id", "tracker");
        var priority = Text("--priority", "優先度名 | id", "priority");
        var queryId = Text("--query", "保存済みクエリ id (指定時は他の絞り込みは無視される)", "id");
        var search = Text("--search", "件名に含まれる文字列", "text");
        var parent = Text("--parent", "親チケット id で絞る (その直下の子チケット)", "id");
        var field = Repeated("--field", "カスタムフィールドの値で絞り込む (完全一致。複数回指定可)", "name=value");
        var sort = new Option<string>("--sort")
        {
            Description = "ソート。カラム名[:desc] をカンマ区切り (id, updated_on, priority, due_date, status など)",
            HelpName = "spec",
            DefaultValueFactory = _ => "updated_on:desc",
        };
        var limit = new Option<int>("--limit") { Description = "最大件数 (上限 100)", HelpName = "n", DefaultValueFactory = _ => 25 };
        var offset = new Option<int>("--offset") { Description = "開始位置 (ページング)", HelpName = "n", DefaultValueFactory = _ => 0 };
        var command = new Command("list", "チケット一覧。既定は対象プロジェクトの未完了チケット (更新日時の新しい順)")
        {
            all, project, status, assignee, mine, tracker, priority, queryId, search, parent, field, sort, limit, offset,
        }.WithNotes("""
            出力 (--json): Redmine の応答そのまま
              { "issues": [ { "id", "subject", "project": {"id","name"}, "tracker", "status", "priority",
                              "author", "assigned_to"?, "done_ratio", "due_date"?, "created_on", "updated_on", ... } ],
                "total_count", "offset", "limit" }

            例:
              redmine issues list                       # 対象プロジェクトの未完了
              redmine issues list --mine                # 自分担当
              redmine issues list --status all --limit 100
              redmine issues list --search "ログイン" --json
              redmine issues list --field "顧客=ACME"   # カスタムフィールドで絞り込む
              redmine issues list --parent 123 --status all   # 123 の子チケット (完了したものも)
              redmine issues list --all --mine          # 全プロジェクトの自分担当
              redmine issues list --offset 25           # 次のページ

            終了コード: 0 成功 / 2 引数誤り / 3 設定不足 / 5 サーバーエラー
            """);
        command.Aliases.Add("ls");
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var allProjects = parse.GetValue(all);
            var otherProject = parse.GetValue(project);
            if (allProjects && otherProject is not null)
            {
                throw new CliException("--all と --project は同時に指定できません", Exit.Usage);
            }
            using var ctx = Context.Create(g, cancellation);
            var query = new Query();
            if (!allProjects)
            {
                query.Set("project_id", otherProject ?? ctx.Config.Project);
            }
            query.Set("status_id", await ctx.Lookup.StatusFilter(parse.GetValue(status)));
            var who = parse.GetValue(mine) ? "me" : parse.GetValue(assignee);
            if (who is not null)
            {
                query.Set("assigned_to_id", who switch
                {
                    "me" => "me",
                    "none" => "!*",
                    _ => (await ctx.Lookup.ResolveAssignee(who)).ToString(CultureInfo.InvariantCulture),
                });
            }
            if (parse.GetValue(tracker) is { Length: > 0 } trackerText)
            {
                query.Set("tracker_id", await ctx.Lookup.ResolveTracker(trackerText));
            }
            if (parse.GetValue(priority) is { Length: > 0 } priorityText)
            {
                query.Set("priority_id", await ctx.Lookup.ResolvePriority(priorityText));
            }
            if (parse.GetValue(queryId) is { Length: > 0 } queryText)
            {
                query.Set("query_id", Context.ParseId(queryText, "query id"));
            }
            if (parse.GetValue(search) is { Length: > 0 } searchText)
            {
                query.Set("subject", $"~{searchText}");
            }
            if (parse.GetValue(parent) is { Length: > 0 } parentText)
            {
                query.Set("parent_id", Context.ParseId(parentText, "--parent"));
            }
            var fields = parse.GetValue(field) ?? [];
            if (fields.Length > 0)
            {
                foreach (var cf in await ctx.Lookup.ResolveCustomFields(fields))
                {
                    query.Set($"cf_{cf.Id}", cf.Value is JsonArray array ? string.Join(",", array.Select(v => v.Text())) : cf.Value.Text());
                }
            }
            query.Set("sort", parse.GetValue(sort));
            query.Set("limit", parse.GetValue(limit) is > 0 and var n ? Math.Min(n, 100) : 25);
            query.Set("offset", Math.Max(0, parse.GetValue(offset)));
            var data = await ctx.Client.GetAsync("/issues.json", query);
            if (g.Json)
            {
                PrintJson(data);
                return Exit.Ok;
            }
            var rows = Items(data.Get("issues"));
            if (rows.Count == 0)
            {
                Out("該当するチケットはありません");
                return Exit.Ok;
            }
            var columns = new List<Column<JsonNode>>
            {
                new("id", i => $"#{i.Get("id").Text()}", Right: true),
                new("トラッカー", i => i.Get("tracker").Name()),
                new("ステータス", i => i.Get("status").Name()),
                new("優先度", i => i.Get("priority").Name()),
                new("担当", i => i.Get("assigned_to").Name()),
                new("件名", i => i.Get("subject").Text(), Max: 60),
                new("更新", i => ShortDate(i.Get("updated_on"))),
            };
            if (allProjects || otherProject is not null)
            {
                columns.Add(new("プロジェクト", i => i.Get("project").Name()));
            }
            Out(Table(rows, [.. columns]));
            var total = data.Get("total_count").Long() ?? rows.Count;
            var next = (data.Get("offset").Long() ?? 0) + rows.Count;
            Out($"({rows.Count} 件表示 / 該当 {total} 件{(next < total ? $"。続きは --offset {next}" : "")})");
            return Exit.Ok;
        });
        return command;
    }

    // ---------- show ----------

    private static Command CreateShow()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var noJournals = new Option<bool>("--no-journals") { Description = "履歴 (コメントと変更履歴) を省略する" };
        var command = new Command("show", "チケットの詳細 (説明、カスタムフィールド、子チケット、関連、添付、履歴)") { id, noJournals }.WithNotes("""
            対象プロジェクト外のチケットも参照できる (その場合は stderr に注意を出す)。

            出力 (--json):
              { "issue": { "id", "subject", "description", "project", "tracker", "status", "priority", "author",
                           "assigned_to"?, "parent"?, "done_ratio", "start_date"?, "due_date"?, "estimated_hours"?,
                           "custom_fields"?: [...], "attachments": [ {"id","filename","filesize","content_type","author","created_on"} ],
                           "journals": [ {"id","user","notes","created_on","private_notes","details":[{"property","name","old_value","new_value"}]} ],
                           "relations"?: [...], "children"?: [...] },
                "in_target": true|false }

            例:
              redmine issues show 123
              redmine issues show 123 --no-journals
              redmine issues show 123 --json

            終了コード: 0 成功 / 2 id が数値でない / 5 存在しない (404) など
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var journals = !parse.GetValue(noJournals);
            using var ctx = Context.Create(g, cancellation);
            string[] include = journals ? ["attachments", "relations", "children", "journals"] : ["attachments", "relations", "children"];
            var issue = await ctx.GetIssueAsync(parse.GetValue(id)!, include);
            var inTarget = await WarnIfOutsideAsync(ctx, issue);
            if (g.Json)
            {
                PrintJson(new JsonObject { ["issue"] = issue.DeepClone(), ["in_target"] = inTarget });
                return Exit.Ok;
            }
            Out(await RenderIssueAsync(ctx, issue, journals));
            return Exit.Ok;
        });
        return command;
    }

    // ---------- create ----------

    private static Command CreateCreate()
    {
        var command = new Command("create", "対象プロジェクトにチケットを作成する");
        var fieldOptions = new FieldOptions(command, update: false);
        var attach = new Option<string[]>("--attach")
        {
            Description = "添付するファイル (複数可)",
            HelpName = "file...",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => [],
        };
        command.Options.Add(attach);
        command.WithNotes($$"""
            {{FieldOptionsHelp}}

            安全装置:
              作成先は常に .redmine.json の project。--project のような指定は存在しない。
              送信前に宛先を stderr に表示し、--yes が無ければ対話で確認する (非対話環境では --yes 必須)。
              --dry-run で送信内容を JSON で確認できる (送信しない。添付もアップロードしない)。

            出力 (--json): { "issue": { "id", "subject", ... } }   通常: 作成した id と URL

            例:
              redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --priority High --assignee me --dry-run
              redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --priority High --assignee me --yes
              redmine issues create --subject "設計メモ" --description-file ./memo.md --attach ./fig1.png ./fig2.png --yes
              redmine issues create --subject "障害" --field "顧客=ACME" --field "対象OS=Windows" --field "対象OS=Linux" --yes
              echo "本文" | redmine issues create --subject "件名" --description-file - --yes --json

            終了コード: 0 作成 / 2 引数誤り・名前解決失敗 / 3 設定不足 / 4 確認できず未送信 / 5 サーバーが拒否 (422 など)
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var values = fieldOptions.Read(parse);
            using var ctx = Context.Create(g, cancellation);
            var target = await ctx.TargetProjectAsync();
            var (fields, custom) = await BuildIssueFieldsAsync(ctx, values);
            var issue = new JsonObject { ["project_id"] = target.Id };
            foreach (var (key, value) in fields)
            {
                issue[key] = value?.DeepClone();
            }
            var attachments = (parse.GetValue(attach) ?? []).Select(Path.GetFullPath).ToList();
            foreach (var file in attachments)
            {
                if (!File.Exists(file))
                {
                    throw new CliException($"添付ファイルが見つかりません: {file}", Exit.Usage);
                }
            }
            var lines = DescribeFields(fields, custom);
            if (attachments.Count > 0)
            {
                lines.Add($"添付: {string.Join(", ", attachments)}");
            }
            var request = new JsonObject
            {
                ["method"] = "POST",
                ["path"] = "/issues.json",
                ["body"] = new JsonObject { ["issue"] = issue.DeepClone() },
                ["attachments"] = new JsonArray([.. attachments.Select(a => (JsonNode)a)]),
            };
            if (!await ctx.ConfirmWriteAsync("チケット作成", lines, request, g))
            {
                return Exit.Ok;
            }
            if (attachments.Count > 0)
            {
                var uploads = new JsonArray();
                foreach (var file in attachments)
                {
                    Info($"アップロード中: {file}");
                    uploads.Add((JsonNode)await ctx.Client.UploadAsync(file));
                }
                issue["uploads"] = uploads;
            }
            var res = await ctx.Client.PostAsync("/issues.json", new JsonObject { ["issue"] = issue });
            if (g.Json)
            {
                PrintJson(res);
                return Exit.Ok;
            }
            var created = RedmineClient.Expect(res, "issue");
            Out($"作成しました: #{created.Get("id").Text()} {created.Get("subject").Text()}");
            Out(IssueUrl(ctx, created.Get("id")));
            return Exit.Ok;
        });
        return command;
    }

    // ---------- update / comment ----------

    private static Command CreateUpdate()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var command = new Command("update", "チケットを更新する (対象プロジェクト内のみ)") { id };
        var fieldOptions = new FieldOptions(command, update: true);
        var note = Text("--note", "コメント (履歴に残る)", "text");
        var noteFile = Text("--note-file", "コメントをファイルから読む (- で stdin)", "file");
        var privateNote = new Option<bool>("--private-note") { Description = "コメントをプライベートにする" };
        command.Options.Add(note);
        command.Options.Add(noteFile);
        command.Options.Add(privateNote);
        command.WithNotes($$"""
            {{FieldOptionsHelp}}

            安全装置:
              更新前にチケットを取得し、所属プロジェクトが .redmine.json の project と一致しなければ拒否する (終了コード 4)。
              送信前に宛先と変更内容を stderr に表示し、--yes が無ければ対話で確認する。--dry-run で送信内容を確認できる。

            出力 (--json): 更新後の { "issue": {...} }   通常: 更新した id と URL

            例:
              redmine issues update 123 --status "進行中" --assignee me --done 30 --note "着手しました" --yes
              redmine issues update 123 --due-date 2026-10-31 --dry-run
              redmine issues update 123 --note-file ./report.md --yes
              redmine issues update 123 --assignee none --yes      # 担当を外す
              redmine issues update 123 --field "確認済み=yes" --field "顧客=" --yes   # カスタムフィールドを設定・クリア

            終了コード: 0 更新 / 2 引数誤り・変更なし / 4 対象プロジェクト外・確認できず / 5 サーバーが拒否
            """);
        command.SetAction((parse, cancellation) => RunUpdateAsync(
            parse.GetValue(id)!,
            fieldOptions.Read(parse),
            parse.GetValue(note),
            parse.GetValue(noteFile),
            parse.GetValue(privateNote),
            "チケット更新",
            GlobalOptions.Of(parse),
            cancellation));
        return command;
    }

    private static Command CreateComment()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var text = new Argument<string?>("text") { Description = "コメント本文", Arity = ArgumentArity.ZeroOrOne };
        var file = Text("--file", "コメントをファイルから読む (- で stdin)", "file");
        var privateNote = new Option<bool>("--private-note") { Description = "プライベートコメントにする" };
        var command = new Command("comment", "チケットにコメントを追加する (update --note の短縮形)") { id, text, file, privateNote }.WithNotes("""
            例:
              redmine issues comment 123 "確認しました。来週対応します" --yes
              redmine issues comment 123 --file ./result.md --yes
              git log -1 | redmine issues comment 123 --file - --yes

            終了コード: update と同じ
            """);
        command.SetAction((parse, cancellation) =>
        {
            var body = parse.GetValue(text);
            var bodyFile = parse.GetValue(file);
            if (body is null && bodyFile is null)
            {
                throw new CliException("コメント本文を引数か --file で指定してください", Exit.Usage);
            }
            return RunUpdateAsync(parse.GetValue(id)!, FieldValues.None, body, bodyFile, parse.GetValue(privateNote), "コメント追加",
                GlobalOptions.Of(parse), cancellation);
        });
        return command;
    }

    private static async Task<int> RunUpdateAsync(
        string id, FieldValues values, string? note, string? noteFile, bool privateNote, string action, Globals g, CancellationToken cancellation)
    {
        using var ctx = Context.Create(g, cancellation);
        var current = await ctx.GetIssueInTargetAsync(id);
        var (fields, custom) = await BuildIssueFieldsAsync(ctx, values);
        if (Input.ReadTextOption(note, noteFile, "note") is { } text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new CliException("コメントが空です", Exit.Usage);
            }
            fields["notes"] = text;
            if (privateNote)
            {
                fields["private_notes"] = true;
            }
        }
        if (fields.Count == 0)
        {
            throw new CliException("変更内容が指定されていません", Exit.Usage, "--status, --note などを指定してください。--help で一覧を表示します。");
        }
        var issueId = current.Get("id").Text();
        var lines = new List<string> { $"#{issueId} {current.Get("subject").Text()}" };
        lines.AddRange(DescribeFields(fields, custom));
        var path = $"/issues/{issueId}.json";
        var request = new JsonObject
        {
            ["method"] = "PUT",
            ["path"] = path,
            ["body"] = new JsonObject { ["issue"] = fields.DeepClone() },
        };
        if (!await ctx.ConfirmWriteAsync(action, lines, request, g))
        {
            return Exit.Ok;
        }
        await ctx.Client.PutAsync(path, new JsonObject { ["issue"] = fields });
        if (g.Json)
        {
            PrintJson(await ctx.Client.GetAsync(path));
            return Exit.Ok;
        }
        Out($"更新しました: #{issueId} {current.Get("subject").Text()}");
        Out(IssueUrl(ctx, current.Get("id")));
        return Exit.Ok;
    }

    // ---------- files ----------

    private static Command CreateFiles()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var command = new Command("files", "チケットの添付ファイル一覧") { id }.WithNotes("""
            出力 (--json): { "issue_id", "attachments": [ { "id", "filename", "filesize", "content_type", "description", "author", "created_on" } ] }

            例:
              redmine issues files 123
              redmine issues files 123 --json
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var issue = await ctx.GetIssueAsync(parse.GetValue(id)!, "attachments");
            await WarnIfOutsideAsync(ctx, issue);
            var attachments = Items(issue.Get("attachments"));
            if (g.Json)
            {
                PrintJson(new JsonObject
                {
                    ["issue_id"] = issue.Get("id")?.DeepClone(),
                    ["attachments"] = new JsonArray([.. attachments.Select(a => a.DeepClone())]),
                });
                return Exit.Ok;
            }
            if (attachments.Count == 0)
            {
                Out($"#{issue.Get("id").Text()} に添付ファイルはありません");
                return Exit.Ok;
            }
            Out(AttachmentTable(attachments));
            return Exit.Ok;
        });
        return command;
    }

    // ---------- attach ----------

    private static Command CreateAttach()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var files = new Argument<string[]>("file") { Description = "添付するファイル (複数可)", Arity = ArgumentArity.OneOrMore };
        var note = Text("--note", "添付と同時に付けるコメント", "text");
        var description = Text("--description", "添付ファイルの説明 (全ファイル共通)", "text");
        var command = new Command("attach", "チケットにファイルを添付する (対象プロジェクト内のみ)") { id, files, note, description }.WithNotes("""
            動作: 各ファイルを POST /uploads.json で送ってトークンを得てから、チケット更新で紐付ける。
                  サイズ上限は Redmine の設定 (既定 5MB) とプロキシに依存する。超えると終了コード 5。

            出力 (--json): 更新後の { "issue": { ..., "attachments": [...] } }

            例:
              redmine issues attach 123 ./screenshot.png --yes
              redmine issues attach 123 ./log1.txt ./log2.txt --note "ログを添付" --yes

            終了コード: 0 添付 / 2 ファイルなし / 4 対象プロジェクト外・確認できず / 5 サイズ超過など
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            var paths = (parse.GetValue(files) ?? []).Select(Path.GetFullPath).ToList();
            foreach (var file in paths)
            {
                if (!File.Exists(file))
                {
                    throw new CliException($"ファイルが見つかりません: {file}", Exit.Usage);
                }
            }
            var noteText = parse.GetValue(note);
            using var ctx = Context.Create(g, cancellation);
            var current = await ctx.GetIssueInTargetAsync(parse.GetValue(id)!);
            var issueId = current.Get("id").Text();
            var lines = new List<string> { $"#{issueId} {current.Get("subject").Text()}" };
            lines.AddRange(paths.Select(p => $"添付: {p} ({FormatSize(new FileInfo(p).Length)})"));
            if (!string.IsNullOrEmpty(noteText))
            {
                lines.Add($"コメント: {Summarize(noteText)}");
            }
            var preview = new JsonObject { ["uploads"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject { ["filename"] = Path.GetFileName(p) })]) };
            if (noteText is not null)
            {
                preview["notes"] = noteText;
            }
            var apiPath = $"/issues/{issueId}.json";
            var request = new JsonObject
            {
                ["method"] = "PUT",
                ["path"] = apiPath,
                ["body"] = new JsonObject { ["issue"] = preview },
                ["attachments"] = new JsonArray([.. paths.Select(p => (JsonNode)p)]),
            };
            if (!await ctx.ConfirmWriteAsync("ファイル添付", lines, request, g))
            {
                return Exit.Ok;
            }
            var uploads = new JsonArray();
            foreach (var p in paths)
            {
                Info($"アップロード中: {p}");
                uploads.Add((JsonNode)await ctx.Client.UploadAsync(p, parse.GetValue(description)));
            }
            var issue = new JsonObject { ["uploads"] = uploads };
            if (!string.IsNullOrEmpty(noteText))
            {
                issue["notes"] = noteText;
            }
            await ctx.Client.PutAsync(apiPath, new JsonObject { ["issue"] = issue });
            var after = await ctx.Client.GetAsync(apiPath, new Query { { "include", "attachments" } });
            if (g.Json)
            {
                PrintJson(after);
                return Exit.Ok;
            }
            Out($"添付しました: #{issueId} に {paths.Count} 件");
            Out(AttachmentTable(Items(after.Get("issue").Get("attachments"))));
            return Exit.Ok;
        });
        return command;
    }

    // ---------- download ----------

    private static Command CreateDownload()
    {
        var id = new Argument<string>("id") { Description = "チケット id (123 か #123)" };
        var name = Text("--name", "ファイル名で選ぶ", "filename");
        var attachment = Text("--attachment", "添付 id で選ぶ (files で確認)", "id");
        var all = new Option<bool>("--all") { Description = "全部保存する" };
        var dir = new Option<string>("--dir") { Description = "保存先ディレクトリ", HelpName = "path", DefaultValueFactory = _ => "." };
        var force = new Option<bool>("--force") { Description = "同名ファイルがあれば上書きする" };
        var command = new Command("download", "チケットの添付ファイルを保存する") { id, name, attachment, all, dir, force }.WithNotes("""
            添付が 1 件だけなら選択指定なしで保存する。複数あるときは --name / --attachment / --all のいずれかが必要。
            同名ファイルが既にあれば上書きせず終了コード 1 (上書きは --force)。
            添付 id は全プロジェクト共通の通し番号なので、必ずチケット id 経由で指定する。

            出力 (--json): { "issue_id", "downloaded": [ { "id", "filename", "path", "filesize" } ] }

            例:
              redmine issues download 123 --all --dir ./tmp/123
              redmine issues download 123 --name spec.xlsx
              redmine issues download 123 --attachment 456 --force

            終了コード: 0 保存 / 1 上書き拒否・IO エラー / 2 選択が曖昧 / 5 サーバーエラー
            """);
        command.SetAction(async (parse, cancellation) =>
        {
            var g = GlobalOptions.Of(parse);
            using var ctx = Context.Create(g, cancellation);
            var issue = await ctx.GetIssueAsync(parse.GetValue(id)!, "attachments");
            await WarnIfOutsideAsync(ctx, issue);
            var issueId = issue.Get("id").Text();
            var attachments = Items(issue.Get("attachments"));
            if (attachments.Count == 0)
            {
                throw new CliException($"#{issueId} に添付ファイルはありません", Exit.Usage);
            }
            var candidates = string.Join(", ", attachments.Select(a => $"{a.Get("filename").Text()} (id {a.Get("id").Text()})"));
            List<JsonNode> selected;
            if (parse.GetValue(all))
            {
                selected = attachments;
            }
            else if (parse.GetValue(attachment) is { } attachmentText)
            {
                var aid = Context.ParseId(attachmentText, "--attachment");
                selected = attachments.Where(a => a.Get("id").Long() == aid).ToList();
            }
            else if (parse.GetValue(name) is { } fileName)
            {
                selected = attachments.Where(a => a.Get("filename").Str() == fileName).ToList();
            }
            else if (attachments.Count == 1)
            {
                selected = attachments;
            }
            else
            {
                throw new CliException(
                    $"#{issueId} には添付が {attachments.Count} 件あります。--name / --attachment / --all で選んでください",
                    Exit.Usage,
                    $"候補: {candidates}");
            }
            if (selected.Count == 0)
            {
                throw new CliException("指定に合う添付がありません", Exit.Usage, $"候補: {candidates}");
            }
            var target = Path.GetFullPath(parse.GetValue(dir) ?? ".");
            Directory.CreateDirectory(target);
            var downloaded = new JsonArray();
            foreach (var a in selected)
            {
                var filename = a.Get("filename").Text();
                var dest = Path.Combine(target, SafeFileName(filename, a.Get("id").Text()));
                if (File.Exists(dest) && !parse.GetValue(force))
                {
                    throw new CliException($"既に存在します: {dest}", Exit.Error, "上書きするには --force を付けてください。");
                }
                Info($"保存中: {filename} ({FormatSize(a.Get("filesize"))})");
                await ctx.Client.DownloadAsync(a.Get("id").Long() ?? 0, filename, dest);
                downloaded.Add((JsonNode)new JsonObject
                {
                    ["id"] = a.Get("id")?.DeepClone(),
                    ["filename"] = filename,
                    ["path"] = dest,
                    ["filesize"] = a.Get("filesize")?.DeepClone(),
                });
            }
            if (g.Json)
            {
                PrintJson(new JsonObject { ["issue_id"] = issue.Get("id")?.DeepClone(), ["downloaded"] = downloaded });
                return Exit.Ok;
            }
            foreach (var d in downloaded)
            {
                Out(d.Get("path").Text());
            }
            return Exit.Ok;
        });
        return command;
    }

    /// <summary>The attachment's name without any directory part, with characters Windows does not allow in names replaced.</summary>
    private static string SafeFileName(string filename, string id)
    {
        var name = Path.GetFileName(filename.Replace('\\', '/').Split('/')[^1]);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim() is { Length: > 0 } and not ("." or "..") ? name : $"attachment-{id}";
    }
}
