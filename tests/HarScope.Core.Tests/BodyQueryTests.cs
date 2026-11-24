using System.Text.Json;
using HarScope.Core.Querying;

namespace HarScope.Core.Tests;

public class BodyQueryTests
{
    /// <summary>A dashboard-like response: 3 teams × risks, plus an error-free envelope.</summary>
    private const string DashboardJson = """
        {"data":{"year":2025,"data":[
          {"team":"OPS","risk":"Operational","count":10},
          {"team":"OPS","risk":"Compliance","count":26},
          {"team":"ENG","risk":"Operational","count":50},
          {"team":"ENG","risk":"Strategic","count":31},
          {"team":"IT","risk":"Operational","count":7,"note":null}
        ]},"errors":null,"meta":{"weird key":"x"}}
        """;

    private static (TestRoot Root, BodyQueryService Service) Setup(string responseJson, string? requestJson = null)
    {
        var t = new TestRoot();
        var content = JsonSerializer.Serialize(new { size = responseJson.Length, mimeType = "application/json", text = responseJson });
        var entry = HarText.Entry("https://api.test/dash", "POST", content: content);
        if (requestJson is not null)
        {
            var post = JsonSerializer.Serialize(new { mimeType = "application/json", text = requestJson });
            entry = entry.Replace("\"bodySize\":0}", $"\"bodySize\":{requestJson.Length},\"postData\":{post}}}");
        }
        t.Write("dash.har", HarText.Of(entry));
        t.Store.Open("dash.har");
        return (t, new BodyQueryService(t.Options));
    }

    private static BodyQueryResult Query(TestRoot t, BodyQueryService s, BodyQuery q)
    {
        var (h, e) = t.Store.GetEntry("dash:entry-1");
        return s.Query(h, e, q);
    }

    private static string Json(object? value) => HarJson.Serialize(value);

    [Fact]
    public void Extracts_a_single_value()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.year" });

        Assert.True(r.Available);
        Assert.Equal(1, r.Matched);
        var v = Assert.Single(r.Values!);
        Assert.Equal("$.data.year", v.Path);
        Assert.Equal("2025", Json(v.Value));
    }

    [Theory]
    [InlineData("$.data.data[0].team", "\"OPS\"")]
    [InlineData("data.data[-1].team", "\"IT\"")]
    [InlineData("$['data']['data'][2]['count']", "50")]
    [InlineData("$.meta['weird key']", "\"x\"")]
    [InlineData("$.errors", "null")]
    public void Supports_documented_path_syntax(string path, string expected)
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = path });
        Assert.Equal(expected, Json(Assert.Single(r.Values!).Value));
    }

    [Fact]
    public void Wildcards_and_slices_return_concrete_paths()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;

        var all = Query(t, s, new BodyQuery { Path = "$.data.data[*].team" });
        Assert.Equal(5, all.Matched);
        Assert.Equal(["OPS", "OPS", "ENG", "ENG", "IT"], all.Values!.Select(v => (string)v.Value!).ToArray());
        Assert.Equal("$.data.data[3].team", all.Values![3].Path);

        var slice = Query(t, s, new BodyQuery { Path = "$.data.data[1:3].risk" });
        Assert.Equal(["$.data.data[1].risk", "$.data.data[2].risk"], slice.Values!.Select(v => v.Path).ToArray());

        var objectWildcard = Query(t, s, new BodyQuery { Path = "$.data.*" });
        Assert.Equal(2, objectWildcard.Matched);
    }

    [Theory]
    [InlineData("$..team")]
    [InlineData("$.data[?(@.x)]")]
    [InlineData("$.data[0:4:2]")]
    [InlineData("$.data[")]
    public void Rejects_unsupported_syntax_with_a_clear_message(string path)
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var ex = Assert.Throws<HarScopeException>(() => Query(t, s, new BodyQuery { Path = path }));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("Supported:", ex.Message);
    }

    [Fact]
    public void Projects_fields_from_each_match()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.data[*]", Fields = ["team", "count", "missing"] });

        Assert.Equal("""{"team":"OPS","count":10,"missing":null}""", Json(r.Values![0].Value));
    }

    [Fact]
    public void Paginates_matches()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var page = Query(t, s, new BodyQuery { Path = "$.data.data[*].count", Limit = 2, Offset = 2 });

        Assert.Equal(5, page.Matched);
        Assert.Equal(2, page.ReturnedCount);
        Assert.True(page.HasMore);
        Assert.Equal(4, page.NextOffset);
        Assert.Equal(["50", "31"], page.Values!.Select(v => Json(v.Value)).ToArray());
    }

    [Theory]
    [InlineData(BodyAggregate.Count, 5)]
    [InlineData(BodyAggregate.Sum, 124)]
    [InlineData(BodyAggregate.Min, 7)]
    [InlineData(BodyAggregate.Max, 50)]
    [InlineData(BodyAggregate.Avg, 24.8)]
    public void Aggregates_numbers(BodyAggregate fn, double expected)
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.data[*].count", Aggregate = fn });

        Assert.Null(r.Values);
        Assert.Equal((decimal)expected, r.Aggregate!.Value);
    }

    [Fact]
    public void Aggregates_per_group_with_value_field()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery
        {
            Path = "$.data.data[*]", Aggregate = BodyAggregate.Sum, ValueField = "count", GroupBy = "team",
        });

        var a = r.Aggregate!;
        Assert.Equal(124, a.Value);
        Assert.Equal(3, a.TotalGroups);
        Assert.Equal(["ENG:81:2", "OPS:36:2", "IT:7:1"], a.Groups!.Select(g => $"{g.Key}:{g.Value}:{g.Count}").ToArray());
    }

    [Fact]
    public void Distinct_counts_values()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.data[*].risk", Aggregate = BodyAggregate.Distinct });

        Assert.Equal(3, r.Aggregate!.TotalGroups);
        Assert.Equal("Operational", r.Aggregate.Groups![0].Key);
        Assert.Equal(3, r.Aggregate.Groups[0].Count);
    }

    [Fact]
    public void Non_numeric_values_are_skipped_and_reported()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.data[*].team", Aggregate = BodyAggregate.Sum });
        Assert.Null(r.Aggregate!.Value);
        Assert.Equal(5, r.Aggregate.Skipped);
    }

    [Fact]
    public void Structure_describes_shape_without_values()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Mode = BodyQueryMode.Structure });
        var json = Json(r.Structure);

        Assert.Contains("\"length\":5", json);
        Assert.Contains("\"team\":\"string\"", json);
        Assert.Contains("\"count\":\"number\"", json);
        Assert.Contains("\"note\":\"null\"", json); // key seen only in a later item is merged in
        Assert.DoesNotContain("OPS", json);
    }

    [Fact]
    public void Large_values_are_summarised_not_dumped()
    {
        var big = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 400).Select(i => new { id = i, name = $"item-{i}" }) });
        var (t, s) = Setup(big);
        using var _ = t;
        var r = Query(t, s, new BodyQuery());

        Assert.True(r.Truncated);
        Assert.Contains("\"_summary\":\"object\"", Json(r.Values![0].Value));
        Assert.Contains(r.Warnings!, w => w.Contains("summary"));
    }

    [Fact]
    public void Output_limit_shortens_the_page()
    {
        var big = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 300).Select(i => new { id = i, name = new string('x', 150) }) });
        var (t, s) = Setup(big);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.items[*]", Limit = 200 });

        Assert.True(r.Truncated);
        Assert.True(r.HasMore);
        Assert.True(HarJson.Serialize(r).Length <= t.Options.MaxOutputChars);
    }

    [Fact]
    public void Queries_the_request_body()
    {
        var (t, s) = Setup(DashboardJson, """{"year":2025,"teamIds":["a","b"]}""");
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Side = BodySide.Request, Path = "$.teamIds", Aggregate = BodyAggregate.Count });
        Assert.Equal(1, r.Matched);

        var ids = Query(t, s, new BodyQuery { Side = BodySide.Request, Path = "$.teamIds[*]" });
        Assert.Equal(2, ids.Matched);
    }

    [Fact]
    public void Reports_missing_and_non_json_bodies()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var s = new BodyQueryService(t.Options);
        BodyQueryResult Q(int n, BodySide side = BodySide.Response)
        {
            var (h, e) = t.Store.GetEntry($"checkout-flow:entry-{n}");
            return s.Query(h, e, new BodyQuery { Side = side });
        }

        Assert.Contains("not captured", Q(12).Reason);          // app.js without content.text
        Assert.Contains("not valid JSON", Q(1).Reason);         // HTML document
        Assert.Contains("Binary", Q(5).Reason);                 // PNG
        Assert.Contains("form parameters", Q(13, BodySide.Request).Reason);
        Assert.False(Q(9).Available);                           // network error, no response

        var decoded = Q(11);                                    // base64-encoded JSON
        Assert.True(decoded.Available);
        Assert.Equal(1, decoded.Matched);
    }

    [Theory]
    [InlineData("count > 20", 3)]
    [InlineData("count >= 31", 2)]
    [InlineData("team == 'OPS'", 2)]
    [InlineData("team != OPS", 3)]
    [InlineData("risk contains opera", 3)]
    [InlineData("risk notContains opera", 2)]
    [InlineData("risk startsWith Com", 1)]
    [InlineData("note exists", 0)]
    [InlineData("note notExists", 5)]
    [InlineData("missing == 1", 0)]
    [InlineData("missing != 1", 5)]
    public void Where_filters_matches(string where, int expected)
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.data.data[*]", Where = [where] });
        Assert.Equal(expected, r.Matched);
        Assert.Equal(5, r.Scanned);
    }

    [Fact]
    public void Where_combines_with_and_and_feeds_aggregates()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery
        {
            Path = "$.data.data[*]", Where = ["risk == Operational", "count < 50"],
            Aggregate = BodyAggregate.Sum, ValueField = "count",
        });
        Assert.Equal(2, r.Matched);
        Assert.Equal(17, r.Aggregate!.Value);
    }

    [Fact]
    public void Where_on_scalars_uses_at_sign_and_reports_empty_result()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        Assert.Equal(1, Query(t, s, new BodyQuery { Path = "$.data.data[*].count", Where = ["@ > 40"] }).Matched);

        var none = Query(t, s, new BodyQuery { Path = "$.data.data[*]", Where = ["team == NOPE"] });
        Assert.Contains(none.Warnings!, w => w.Contains("where filter"));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("count ~ 3")]
    [InlineData("count >")]
    [InlineData("note exists yes")]
    public void Invalid_where_is_rejected(string where)
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var ex = Assert.Throws<HarScopeException>(() => Query(t, s, new BodyQuery { Path = "$.data.data[*]", Where = [where] }));
        Assert.Contains("Form:", ex.Message);
    }

    [Fact]
    public void Path_matching_nothing_gives_a_hint()
    {
        var (t, s) = Setup(DashboardJson);
        using var _ = t;
        var r = Query(t, s, new BodyQuery { Path = "$.nope" });
        Assert.Equal(0, r.Matched);
        Assert.Contains(r.Warnings!, w => w.Contains("structure"));
    }
}
