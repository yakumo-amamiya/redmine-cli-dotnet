using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>An item to pick by id or name (a tracker, a status, a member ...).</summary>
internal sealed record Named(long Id, string Name, JsonObject? Raw = null);

/// <summary>A custom field of the target project's issues. The definition (format and so on) needs admin rights; Detailed tells whether it was read.</summary>
internal sealed record CustomField(
    long Id, string Name, string? Format, bool? Multiple, bool? Required, JsonArray? PossibleValues, List<string> Trackers, bool Detailed)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["format"] = Format,
        ["multiple"] = Multiple,
        ["required"] = Required,
        ["possible_values"] = PossibleValues?.DeepClone(),
        ["trackers"] = new JsonArray([.. Trackers.Select(t => (JsonNode)t)]),
        ["detailed"] = Detailed,
    };
}

/// <summary>A --field value turned into what Redmine takes: a string, or an array for a multiple field.</summary>
internal sealed record ResolvedCustomField(long Id, string Name, JsonNode Value);

/// <summary>
/// Names to ids: trackers, statuses, priorities, activities, categories, versions, assignees, custom fields.
/// A number is taken as an id; text is matched by name (case-insensitive exact match, then a unique partial match).
/// Each list is read from the server once per command.
/// </summary>
internal sealed partial class Lookups(Context ctx)
{
    private Task<List<Named>>? _trackers;
    private Task<List<Named>>? _statuses;
    private Task<List<Named>>? _priorities;
    private Task<List<Named>>? _activities;
    private Task<List<Named>>? _categories;
    private Task<List<Named>>? _versions;
    private Task<List<Named>>? _members;
    private Task<List<CustomField>>? _customFields;

    public Task<List<Named>> Trackers() => _trackers ??= LoadAsync("/trackers.json", "trackers");

    public Task<List<Named>> Statuses() => _statuses ??= LoadAsync("/issue_statuses.json", "issue_statuses");

    public Task<List<Named>> Priorities() => _priorities ??= LoadAsync("/enumerations/issue_priorities.json", "issue_priorities");

    public Task<List<Named>> Activities() => _activities ??= LoadAsync("/enumerations/time_entry_activities.json", "time_entry_activities");

    public Task<List<Named>> Categories() => _categories ??= LoadProjectAsync("issue_categories.json", "issue_categories");

    public Task<List<Named>> Versions() => _versions ??= LoadProjectAsync("versions.json", "versions");

    public Task<List<Named>> Members() => _members ??= LoadMembersAsync();

    public Task<List<CustomField>> CustomFields() => _customFields ??= LoadCustomFieldsAsync();

    private async Task<List<Named>> LoadAsync(string apiPath, string key) => ToNamed(await ctx.Client.GetAsync(apiPath), key);

    private async Task<List<Named>> LoadProjectAsync(string file, string key)
    {
        var p = await ctx.TargetProjectAsync();
        return ToNamed(await ctx.Client.GetAsync($"/projects/{p.Id}/{file}"), key);
    }

    private static List<Named> ToNamed(JsonNode? data, string key) =>
        (data.Get(key) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(x => x.Get("id").Long() is not null)
            .Select(x => new Named(x.Get("id").Long()!.Value, x.Name(), x))
            .ToList();

    private async Task<List<Named>> LoadMembersAsync()
    {
        var p = await ctx.TargetProjectAsync();
        var (items, _) = await ctx.Client.ListAllAsync($"/projects/{p.Id}/memberships.json", "memberships");
        return items
            .Select(m => m.Get("user") ?? m.Get("group"))
            .Where(who => who.Get("id").Long() is not null)
            .Select(who => new Named(who.Get("id").Long()!.Value, who.Name()))
            .ToList();
    }

    /// <summary>
    /// The custom fields usable in the target project's issues.
    /// - Which ones: from the project (include=issue_custom_fields). A member can read it, but it has id and name only.
    /// - Format, required, multiple and choices: from /custom_fields.json, which needs admin rights; without them Detailed is false.
    /// </summary>
    private async Task<List<CustomField>> LoadCustomFieldsAsync()
    {
        var p = await ctx.TargetProjectAsync();
        var data = await ctx.Client.GetAsync($"/projects/{p.Id}.json", new Query { { "include", "issue_custom_fields" } });
        var fields = (data.Get("project").Get("issue_custom_fields") as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(cf => cf.Get("id").Long() is not null)
            .Select(cf => new CustomField(cf.Get("id").Long()!.Value, cf.Name(), null, null, null, null, [], false))
            .ToList();
        JsonArray? details = null;
        try
        {
            details = (await ctx.Client.GetAsync("/custom_fields.json")).Get("custom_fields") as JsonArray ?? [];
        }
        catch (HttpStatusException e) when (e.Status is 403 or 404)
        {
            // Not an admin: names only.
        }
        if (details is null)
        {
            return fields;
        }
        var byId = details
            .OfType<JsonObject>()
            .Where(cf => cf.Get("customized_type").Str() == "issue" && cf.Get("id").Long() is not null)
            .GroupBy(cf => cf.Get("id").Long()!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        return fields
            .Select(f => byId.TryGetValue(f.Id, out var d)
                ? f with
                {
                    Format = d.Get("field_format").Str(),
                    Multiple = d.Get("multiple").Truthy(),
                    Required = d.Get("is_required").Truthy(),
                    PossibleValues = d.Get("possible_values") as JsonArray,
                    Trackers = (d.Get("trackers") as JsonArray ?? []).Select(t => t.Name()).ToList(),
                    Detailed = true,
                }
                : f)
            .ToList();
    }

    /// <summary>The id in list matching input (an id, an exact name, or a unique part of a name).</summary>
    public static long Resolve(IReadOnlyList<Named> list, string input, string label)
    {
        var text = input.Trim();
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            var hit = long.TryParse(text, out var id) ? list.FirstOrDefault(x => x.Id == id) : null;
            return hit?.Id ?? throw new CliException($"{label} id={text} は選択肢にありません", Exit.Usage, $"選べる{label}: {Describe(list)}");
        }
        var lower = text.ToLowerInvariant();
        var exact = list.Where(x => x.Name.ToLowerInvariant() == lower).ToList();
        if (exact.Count == 1)
        {
            return exact[0].Id;
        }
        var partial = list.Where(x => x.Name.ToLowerInvariant().Contains(lower, StringComparison.Ordinal)).ToList();
        if (partial.Count == 1)
        {
            return partial[0].Id;
        }
        if (partial.Count > 1)
        {
            throw new CliException(
                $"{label}「{text}」に複数の候補があります: {string.Join(", ", partial.Select(x => x.Name))}",
                Exit.Usage,
                "名前を完全に書くか、id で指定してください。");
        }
        throw new CliException($"{label}「{text}」は見つかりません", Exit.Usage, $"選べる{label}: {Describe(list)}");
    }

    private static string Describe(IReadOnlyList<Named> list) =>
        list.Count == 0 ? "(なし)" : string.Join(", ", list.Select(x => $"{x.Name} ({x.Id})"));

    public async Task<long> ResolveTracker(string input) => Resolve(await Trackers(), input, "トラッカー");

    public async Task<long> ResolveStatus(string input) => Resolve(await Statuses(), input, "ステータス");

    public async Task<long> ResolvePriority(string input) => Resolve(await Priorities(), input, "優先度");

    public async Task<long> ResolveActivity(string input) => Resolve(await Activities(), input, "作業分類");

    public async Task<long> ResolveCategory(string input) => Resolve(await Categories(), input, "カテゴリ");

    public async Task<long> ResolveVersion(string input) => Resolve(await Versions(), input, "対象バージョン");

    /// <summary>An assignee: me, a numeric user id, or a member's name (a user or a group).</summary>
    public async Task<long> ResolveAssignee(string input)
    {
        var text = input.Trim();
        if (text.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return (await ctx.CurrentUserAsync()).Get("id").Long() ?? throw new CliException("サーバーの応答にユーザー id がありません", Exit.Http);
        }
        if (text.Length > 0 && text.All(char.IsAsciiDigit) && long.TryParse(text, out var id))
        {
            return id;
        }
        return Resolve(await Members(), text, "担当者");
    }

    /// <summary>The status filter of a list: open / closed / all (*) as they are, anything else by name.</summary>
    public async Task<string> StatusFilter(string? input)
    {
        var text = (input ?? "open").Trim().ToLowerInvariant();
        if (text is "open" or "closed")
        {
            return text;
        }
        if (text is "all" or "*")
        {
            return "*";
        }
        return (await ResolveStatus(input!)).ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^(1|true|yes|y|on)$", RegexOptions.IgnoreCase)]
    private static partial Regex True();

    [GeneratedRegex("^(0|false|no|n|off)$", RegexOptions.IgnoreCase)]
    private static partial Regex False();

    [GeneratedRegex("^-?[0-9]+$")]
    private static partial Regex Integer();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")]
    private static partial Regex Date();

    public static string CheckDate(string value, string label) =>
        Date().IsMatch(value) ? value : throw new CliException($"{label} は YYYY-MM-DD 形式で指定してください: {value}", Exit.Usage);

    /// <summary>A custom field value in the form Redmine takes, by the field's format.</summary>
    private async Task<string> ConvertCustomValue(CustomField field, string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0)
        {
            return "";
        }
        var label = $"カスタムフィールド「{field.Name}」";
        switch (field.Format)
        {
            case "bool":
                if (True().IsMatch(v))
                {
                    return "1";
                }
                if (False().IsMatch(v))
                {
                    return "0";
                }
                throw new CliException($"{label} は yes / no で指定してください: {v}", Exit.Usage);
            case "date":
                return CheckDate(v, label);
            case "int":
                return Integer().IsMatch(v) ? v : throw new CliException($"{label} は整数で指定してください: {v}", Exit.Usage);
            case "float":
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? v
                    : throw new CliException($"{label} は数値で指定してください: {v}", Exit.Usage);
            case "user":
                return (await ResolveAssignee(v)).ToString(CultureInfo.InvariantCulture);
            case "version":
                return (await ResolveVersion(v)).ToString(CultureInfo.InvariantCulture);
            case "list" or "enumeration":
            {
                var options = field.PossibleValues?.OfType<JsonObject>().ToList();
                if (options is null || options.Count == 0)
                {
                    return v;
                }
                var lower = v.ToLowerInvariant();
                static string Shown(JsonObject o) => (o.Get("label") ?? o.Get("value")).Text();
                var exact = options
                    .Where(o => o.Get("value").Text().ToLowerInvariant() == lower || Shown(o).ToLowerInvariant() == lower)
                    .ToList();
                if (exact.Count == 1)
                {
                    return exact[0].Get("value").Text();
                }
                var partial = options.Where(o => Shown(o).ToLowerInvariant().Contains(lower, StringComparison.Ordinal)).ToList();
                if (partial.Count == 1)
                {
                    return partial[0].Get("value").Text();
                }
                throw new CliException(
                    partial.Count > 1 ? $"{label} の「{v}」に複数の候補があります" : $"{label} に「{v}」という選択肢はありません",
                    Exit.Usage,
                    $"選べる値: {string.Join(", ", options.Select(Shown))}");
            }
            default:
                return v;
        }
    }

    /// <summary>
    /// --field "name=value" specs to Redmine's custom_fields ([{ id, value }]). A name given more than once makes a
    /// multiple selection; an empty value clears the field.
    /// </summary>
    public async Task<List<ResolvedCustomField>> ResolveCustomFields(IReadOnlyList<string> specs)
    {
        var fields = await CustomFields();
        if (fields.Count == 0)
        {
            throw new CliException("対象プロジェクトで使えるカスタムフィールドがありません", Exit.Usage, "`redmine fields` で確認できます。");
        }
        var named = fields.Select(f => new Named(f.Id, f.Name)).ToList();
        var order = new List<long>();
        var grouped = new Dictionary<long, (CustomField Field, List<string> Values)>();
        foreach (var spec in specs)
        {
            var eq = spec.IndexOf('=');
            if (eq <= 0)
            {
                throw new CliException($"--field は \"名前=値\" の形で指定してください: {spec}", Exit.Usage);
            }
            var id = Resolve(named, spec[..eq].Trim(), "カスタムフィールド");
            if (!grouped.TryGetValue(id, out var entry))
            {
                entry = (fields.First(f => f.Id == id), []);
                grouped[id] = entry;
                order.Add(id);
            }
            entry.Values.Add(spec[(eq + 1)..]);
        }
        var result = new List<ResolvedCustomField>();
        foreach (var id in order)
        {
            var (field, values) = grouped[id];
            var converted = new List<string>();
            foreach (var value in values)
            {
                converted.Add(await ConvertCustomValue(field, value));
            }
            if (field.Multiple == true || (field.Multiple is null && converted.Count > 1))
            {
                result.Add(new ResolvedCustomField(field.Id, field.Name, new JsonArray([.. converted.Where(v => v.Length > 0).Select(v => (JsonNode)v)])));
            }
            else
            {
                if (converted.Count > 1)
                {
                    throw new CliException($"カスタムフィールド「{field.Name}」は複数の値を取れません", Exit.Usage);
                }
                result.Add(new ResolvedCustomField(field.Id, field.Name, JsonValue.Create(converted[0])));
            }
        }
        return result;
    }
}
