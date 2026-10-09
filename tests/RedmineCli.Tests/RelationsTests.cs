using System.Text.Json.Nodes;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>Related issues (relations / relate / unrelate) and the children filter, against the mock Redmine.</summary>
public sealed class RelationsTests : IClassFixture<RedmineFixture>
{
    // Issue 1's relations as Redmine stores them (one direction each):
    //   10: 1 blocks 2 / 11: 3 precedes 1 (delay 2) / 12: 1 relates 99 (another project) / 13: 2 relates 1
    private const string Relations = """
        [ { "id": 10, "issue_id": 1, "issue_to_id": 2, "relation_type": "blocks", "delay": null },
          { "id": 11, "issue_id": 3, "issue_to_id": 1, "relation_type": "precedes", "delay": 2 },
          { "id": 12, "issue_id": 1, "issue_to_id": 99, "relation_type": "relates", "delay": null },
          { "id": 13, "issue_id": 2, "issue_to_id": 1, "relation_type": "relates", "delay": null } ]
        """;

    private readonly RedmineFixture _f;

    public RelationsTests(RedmineFixture fixture)
    {
        _f = fixture;
        _f.Reset();
        var m = _f.Mock;
        m.Route("GET /issues/1.json", _ => new MockResponse(Json: new JsonObject { ["issue"] = RedmineFixture.Issue(i => i["relations"] = JsonNode.Parse(Relations)) }));
        m.Route("GET /issues/2.json", _ => new MockResponse(Json: new JsonObject { ["issue"] = Inside(2, "second") }));
        m.Route("GET /issues/3.json", _ => new MockResponse(Json: new JsonObject { ["issue"] = Inside(3, "third") }));
        m.Route("GET /issues.json", r =>
        {
            var wanted = r.Query.GetValueOrDefault("issue_id", "").Split(',');
            JsonNode[] all = [Inside(2, "second"), Inside(3, "third"), RedmineFixture.IssueOutside()];
            var found = all.Where(i => wanted.Contains(i["id"]!.ToJsonString())).ToArray();
            return new MockResponse(Json: new JsonObject { ["issues"] = new JsonArray(found), ["total_count"] = found.Length, ["offset"] = 0, ["limit"] = 100 });
        });
        m.Route("POST /issues/1/relations.json", r =>
        {
            var relation = r.Json!["relation"]!.DeepClone().AsObject();
            relation["id"] = 20;
            relation["issue_id"] = 1;
            return new MockResponse(201, new JsonObject { ["relation"] = relation });
        });
        foreach (var id in new[] { 10, 11, 12, 13 })
        {
            m.Route($"DELETE /relations/{id}.json", _ => new MockResponse(204));
        }
    }

    private MockRedmine Mock => _f.Mock;

    private static JsonObject Inside(int id, string subject) => RedmineFixture.Issue(i =>
    {
        i["id"] = id;
        i["subject"] = subject;
        i["status"] = new JsonObject { ["id"] = 2, ["name"] = "In Progress" };
    });

    private Task<CliResult> Run(params string[] args) => RunAsync(_f.Workdir, args);

    [Fact]
    public async Task Relations_are_shown_from_the_issues_side_with_the_other_issues_subject()
    {
        var r = await Run("issues", "relations", "1", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var relations = r.Json["relations"]!.AsArray();
        Assert.Equal(
            new[] { "blocks #2", "follows #3", "relates #99", "relates #2" },
            relations.Select(x => $"{x!["relation_type"]} #{x["other_issue_id"]}"));
        Assert.Equal("ブロック先", relations[0]!["label"]!.GetValue<string>());
        Assert.Equal(2, relations[1]!["delay"]!.GetValue<int>());
        Assert.Equal("second", relations[0]!["other_issue"]!["subject"]!.GetValue<string>());
        Assert.Equal("Other", relations[2]!["other_issue"]!["project"]!["name"]!.GetValue<string>());
        var q = Mock.Hit("GET", "/issues.json")[0].Query;
        Assert.Equal("2,3,99", q["issue_id"]);
        Assert.Equal("*", q["status_id"]);
    }

    [Fact]
    public async Task Relations_print_a_table_with_the_project_when_one_is_elsewhere()
    {
        var r = await Run("issues", "relations", "1");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("ブロック先", r.Stdout);
        Assert.Contains("次のチケットに後続", r.Stdout);
        Assert.Contains("second", r.Stdout);
        Assert.Contains("プロジェクト", r.Stdout);
        Assert.Contains("2 日", r.Stdout);
    }

    [Fact]
    public async Task Show_names_the_relations_from_the_issues_side()
    {
        var r = await Run("issues", "show", "1", "--no-journals");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("ブロック先 (blocks): #2", r.Stdout);
        Assert.Contains("次のチケットに後続 (follows): #3 (遅延 2 日)", r.Stdout);
    }

    [Fact]
    public async Task Relate_posts_the_relation_from_the_first_issue()
    {
        var r = await Run("issues", "relate", "1", "#2", "--type", "blocked-by", "--yes", "--json");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "relation": { "issue_to_id": 2, "relation_type": "blocked" } }""", Mock.Hit("POST", "/issues/1/relations.json")[0].Json);
        Assert.Equal(20, r.Json["relation"]!["id"]!.GetValue<int>());
        Assert.Contains("ブロック元", r.Stderr);
    }

    [Fact]
    public async Task Relate_defaults_to_relates_and_dry_run_sends_nothing()
    {
        var r = await Run("issues", "relate", "1", "3", "--dry-run");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "relation": { "issue_to_id": 3, "relation_type": "relates" } }""", r.Json["request"]!["body"]);
        Assert.Empty(Mock.Hit("POST", "/issues/1/relations.json"));
    }

    [Fact]
    public async Task Relate_precedes_takes_a_delay_and_other_types_refuse_it()
    {
        var r = await Run("issues", "relate", "1", "3", "--type", "precedes", "--delay", "3", "--yes");
        Assert.True(r.Code == 0, r.ToString());
        AssertJson("""{ "relation": { "issue_to_id": 3, "relation_type": "precedes", "delay": 3 } }""", Mock.Hit("POST", "/issues/1/relations.json")[0].Json);
        var refused = await Run("issues", "relate", "1", "3", "--delay", "3", "--yes");
        Assert.Equal(2, refused.Code);
        Assert.Contains("precedes", refused.Stderr);
    }

    [Theory]
    [InlineData("1", "99")]
    [InlineData("99", "1")]
    public async Task Relate_refuses_with_4_when_either_issue_is_outside_the_target(string from, string to)
    {
        var r = await Run("issues", "relate", from, to, "--yes");
        Assert.True(r.Code == 4, r.ToString());
        Assert.Contains("対象プロジェクト「Demo」", r.Stderr);
        Assert.DoesNotContain(Mock.Requests, x => x.Method == "POST");
    }

    [Fact]
    public async Task Relate_refuses_bad_arguments_with_2()
    {
        var same = await Run("issues", "relate", "1", "1", "--yes");
        Assert.Equal(2, same.Code);
        var unknown = await Run("issues", "relate", "1", "2", "--type", "parent", "--yes");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("blocks (ブロック先)", unknown.Stderr);
        Assert.Empty(Mock.Requests);
    }

    [Fact]
    public async Task Unrelate_deletes_the_one_relation_between_the_two()
    {
        var r = await Run("issues", "unrelate", "1", "3", "--yes", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Single(Mock.Hit("DELETE", "/relations/11.json"));
        Assert.Equal("follows", r.Json["deleted"]!["relation_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unrelate_asks_for_the_type_when_there_are_several_and_reads_it_from_the_issues_side()
    {
        var ambiguous = await Run("issues", "unrelate", "1", "2", "--yes");
        Assert.Equal(2, ambiguous.Code);
        Assert.Contains("--type", ambiguous.Stderr);
        Assert.Contains("blocks #2", ambiguous.Stderr);
        Assert.DoesNotContain(Mock.Requests, x => x.Method == "DELETE");

        var blocks = await Run("issues", "unrelate", "1", "2", "--type", "blocks", "--yes");
        Assert.True(blocks.Code == 0, blocks.ToString());
        Assert.Single(Mock.Hit("DELETE", "/relations/10.json"));
        var relates = await Run("issues", "unrelate", "1", "2", "--type", "relates", "--yes");
        Assert.True(relates.Code == 0, relates.ToString());
        Assert.Single(Mock.Hit("DELETE", "/relations/13.json"));
    }

    [Fact]
    public async Task Unrelate_refuses_with_4_when_the_other_issue_is_outside_the_target()
    {
        var r = await Run("issues", "unrelate", "1", "99", "--yes");
        Assert.True(r.Code == 4, r.ToString());
        Assert.DoesNotContain(Mock.Requests, x => x.Method == "DELETE");
    }

    [Fact]
    public async Task Unrelate_without_a_relation_exits_2_with_the_issues_relations()
    {
        var r = await Run("issues", "unrelate", "1", "5", "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("follows #3", r.Stderr);
    }

    [Fact]
    public async Task List_parent_filters_the_children()
    {
        var r = await Run("issues", "list", "--parent", "#1", "--status", "all", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var q = Mock.Hit("GET", "/issues.json")[0].Query;
        Assert.Equal("1", q["parent_id"]);
        Assert.Equal("*", q["status_id"]);
        Assert.Equal("demo", q["project_id"]);
    }

    [Fact]
    public async Task Relations_help_lists_the_types_and_examples()
    {
        var r = await Run("issues", "relate", "--help");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("precedes", r.Stdout);
        Assert.Contains("例:", r.Stdout);
    }
}
