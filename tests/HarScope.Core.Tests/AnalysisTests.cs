using System.Text.Json;
using HarScope.Core.Analysis;
using HarScope.Core.Querying;

namespace HarScope.Core.Tests;

public class AnalysisTests
{
    private static string JsonContent(string json, string mime = "application/json") =>
        JsonSerializer.Serialize(new { size = json.Length, mimeType = mime, text = json });

    private static CompareService Compare(TestRoot t) => new(t.Options, t.Redactor, new JsonBodyCache());

    // ---------- trace flow ----------

    [Fact]
    public void Trace_flow_groups_calls_by_quiet_gaps_and_hides_static_assets()
    {
        using var t = new TestRoot();
        var r = new FlowService(t.Options, t.Redactor).Trace(t.OpenSample(), new FlowQuery());

        Assert.Equal(2, r.HiddenStatic); // png + app.js
        Assert.Equal(7, r.TotalGroups);
        var groups = r.Groups.Select(g => string.Join(",", g.CallList.Select(c => c.Id.Split("entry-")[1]))).ToArray();
        Assert.Equal(["1", "10", "2,3", "4,11", "6", "7,8,13", "9"], groups);

        var payment = r.Groups[5];
        Assert.Equal(1, payment.Failed);
        Assert.Equal(1, payment.Slow);
        Assert.Equal("checkout-flow:entry-8", payment.FirstFailure);
        Assert.Contains("slow", payment.CallList.Single(c => c.Id.EndsWith("entry-8")).Flags!);
        Assert.Equal(1000, payment.CallList.Single(c => c.Id.EndsWith("entry-8")).OffsetMs);
    }

    [Fact]
    public void Trace_flow_reports_confirmed_links_separately_from_heuristics()
    {
        using var t = new TestRoot();
        var r = new FlowService(t.Options, t.Redactor).Trace(t.OpenSample(), new FlowQuery());

        var corr = Assert.Single(r.ConfirmedLinks!, l => l.Kind == "correlation");
        Assert.Equal("x-correlation-id=corr-77", corr.Detail);
        Assert.Equal(["checkout-flow:entry-7", "checkout-flow:entry-8"], corr.Entries);
        Assert.Contains(r.ConfirmedLinks!, l => l.Kind == "redirect" && l.Entries[0] == "checkout-flow:entry-10");
        Assert.Contains(r.Notes, n => n.Contains("heuristic"));
    }

    [Fact]
    public void Trace_flow_gap_and_paging_are_configurable()
    {
        using var t = new TestRoot();
        var flow = new FlowService(t.Options, t.Redactor);

        var coarse = flow.Trace(t.OpenSample(), new FlowQuery { GapMs = 5_000 });
        Assert.True(coarse.TotalGroups < 7);

        var page = flow.Trace(t.OpenSample(), new FlowQuery { Limit = 2, Offset = 2 });
        Assert.Equal(2, page.ReturnedCount);
        Assert.Equal(3, page.Groups[0].Group);
        Assert.Equal(4, page.NextOffset);
    }

    [Fact]
    public void Trace_flow_detects_failure_streaks()
    {
        using var t = new TestRoot();
        t.Write("streak.har", HarText.Of(
            HarText.Entry("https://x.test/a", status: 200, started: "2026-01-01T00:00:00Z"),
            HarText.Entry("https://x.test/b", status: 500, started: "2026-01-01T00:00:01Z"),
            HarText.Entry("https://x.test/c", status: 503, started: "2026-01-01T00:00:02Z"),
            HarText.Entry("https://x.test/d", status: 401, started: "2026-01-01T00:00:03Z")));
        var r = new FlowService(t.Options, t.Redactor).Trace(t.Store.Open("streak.har"), new FlowQuery());

        var streak = Assert.Single(r.FailureStreaks!);
        Assert.Equal(("streak:entry-2", "streak:entry-4", 3), (streak.From, streak.To, streak.Count));
    }

    // ---------- compare entries ----------

    [Fact]
    public void Compare_entries_diffs_status_query_and_json_bodies()
    {
        using var t = new TestRoot();
        t.Write("cmp.har", HarText.Of(
            HarText.Entry("https://x.test/api/order?id=1&page=1", status: 200,
                content: JsonContent("""{"status":"OK","items":[1,2],"meta":{"ts":"a"},"old":true}""")),
            HarText.Entry("https://x.test/api/order?id=2&page=1", status: 500,
                content: JsonContent("""{"status":"FAILED","items":[1,2,3],"meta":{"ts":"b"},"error":"X"}"""))));
        var h = t.Store.Open("cmp.har");
        var (_, a) = t.Store.GetEntry("cmp:entry-1");
        var (_, b) = t.Store.GetEntry("cmp:entry-2");

        var r = Compare(t).CompareEntries(h, a, h, b, ignorePaths: ["$.meta.ts"]);

        Assert.False(r.Identical);
        Assert.Contains("status 200 -> 500", r.Differences!);
        Assert.Equal("id", Assert.Single(r.Request.QueryParams!).Name);
        var diffs = r.Response.Body.Diffs!.Select(d => $"{d.Path}:{d.Change}").ToArray();
        Assert.Equal(["$.status:changed", "$.items.length:changed", "$.items[2]:added", "$.old:removed", "$.error:added"], diffs);
        Assert.Equal(5, r.Response.Body.TotalDiffs);
    }

    [Fact]
    public void Compare_entries_on_identical_calls_and_redacts_header_values()
    {
        using var t = new TestRoot();
        var h = t.OpenSample();
        var (_, profile) = t.Store.GetEntry("checkout-flow:entry-3");
        var (_, products) = t.Store.GetEntry("checkout-flow:entry-4");

        var self = Compare(t).CompareEntries(h, profile, h, profile);
        Assert.True(self.Identical);
        Assert.Null(self.Differences);

        var r = Compare(t).CompareEntries(h, profile, h, products);
        Assert.Contains(r.Warnings!, w => w.Contains("different endpoints"));
        var json = HarJson.Serialize(r);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.", json);
        Assert.DoesNotContain("k-123456", json);
        Assert.True(r.IgnoredHeaderChanges >= 1); // x-request-id only on profile
    }

    [Fact]
    public void Compare_text_bodies_reports_first_difference()
    {
        using var t = new TestRoot();
        t.Write("txt.har", HarText.Of(
            HarText.Entry("https://x.test/p", content: JsonContent("<p>hello world</p>", "text/html")),
            HarText.Entry("https://x.test/p", content: JsonContent("<p>hello there</p>", "text/html"))));
        var h = t.Store.Open("txt.har");
        var (_, a) = t.Store.GetEntry("txt:entry-1");
        var (_, b) = t.Store.GetEntry("txt:entry-2");

        var body = Compare(t).CompareEntries(h, a, h, b).Response.Body;
        Assert.Equal("text", body.ComparedAs);
        Assert.Equal(9, body.FirstDifferenceAt);
    }

    // ---------- compare captures ----------

    [Fact]
    public void Compare_captures_pairs_calls_and_lists_unmatched()
    {
        using var t = new TestRoot();
        t.Write("before.har", HarText.Of(
            HarText.Entry("https://dev.test/api/a?y=2&x=1", content: JsonContent("""{"v":1}""")),
            HarText.Entry("https://dev.test/api/b", "POST", 200, content: JsonContent("""{"ok":true}""")),
            HarText.Entry("https://dev.test/api/same", content: JsonContent("""{"n":5}""")),
            HarText.Entry("https://dev.test/api/only-a")));
        t.Write("after.har", HarText.Of(
            HarText.Entry("https://staging.test/api/a?x=1&y=2", content: JsonContent("""{"v":2}""")),
            HarText.Entry("https://staging.test/api/b", "POST", 500, content: JsonContent("""{"ok":false}""")),
            HarText.Entry("https://staging.test/api/same", content: JsonContent("""{"n":5}""")),
            HarText.Entry("https://staging.test/logo.png", content: JsonContent("x", "image/png")),
            HarText.Entry("https://staging.test/api/only-b")));
        var a = t.Store.Open("before.har");
        var b = t.Store.Open("after.har");

        var r = Compare(t).CompareCaptures(a, b, new CaptureCompareQuery());

        Assert.Equal(3, r.Pairs);
        Assert.Equal(2, r.DifferentPairs);
        Assert.Equal(1, r.IdenticalPairs);
        Assert.Equal(1, r.StatusChanged);
        Assert.Equal("POST /api/b", r.PairRows[0].Key); // status change ranks first
        Assert.Equal("GET /api/a?x=1&y=2", r.PairRows[1].Key); // query order and host ignored
        Assert.Equal("different", r.PairRows[1].Response);
        Assert.Equal("same", r.PairRows[2].Response);
        Assert.Equal("GET /api/only-a", Assert.Single(r.OnlyInA).Key);
        Assert.Equal("GET /api/only-b", Assert.Single(r.OnlyInB).Key); // png skipped as static

        var onlyDifferent = Compare(t).CompareCaptures(a, b, new CaptureCompareQuery { DifferentOnly = true });
        Assert.Equal(2, onlyDifferent.ReturnedCount);

        var ignoring = Compare(t).CompareCaptures(a, b, new CaptureCompareQuery { IgnorePaths = ["$.v"], PathContains = "/api/a" });
        Assert.Equal(1, ignoring.IdenticalPairs);
    }

    // ---------- repro ----------

    [Fact]
    public void Repro_replaces_secrets_with_placeholders()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var items = new[] { "checkout-flow:entry-4", "checkout-flow:entry-2", "checkout-flow:entry-3" }
            .Select(id => t.Store.GetEntry(id)).ToList();

        var r = new ReproService(t.Options, t.Redactor, new JsonBodyCache())
            .Build(items.Select(i => (i.Handle, i.Entry)).ToList(), ReproFormat.Http, allHeaders: false);
        var all = string.Join("\n", r.Requests.Select(q => q.Text));

        Assert.Equal(["checkout-flow:entry-2", "checkout-flow:entry-3", "checkout-flow:entry-4"], r.Requests.Select(q => q.Id).ToArray());
        Assert.Contains("POST https://api.example.test/auth/login", all);
        Assert.Contains("\"password\":\"{{password}}\"", all);
        Assert.Contains("\"username\":\"demo\"", all);
        Assert.Contains("authorization: Bearer {{authorization}}", all);
        Assert.Contains("Cookie: {{cookie}}", all);
        Assert.Contains("api_key={{api_key}}", all);
        Assert.Contains("q=shoes", all);
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.", all);
        Assert.DoesNotContain("k-123456", all);
        Assert.DoesNotContain("x-request-id", all); // volatile header dropped in minimal mode
        Assert.Contains(r.Variables!, v => v.Name == "password");
    }

    [Fact]
    public void Repro_finds_values_produced_by_earlier_responses()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var items = new[] { "checkout-flow:entry-3", "checkout-flow:entry-8" }.Select(id => t.Store.GetEntry(id)).ToList();

        var r = new ReproService(t.Options, t.Redactor, new JsonBodyCache())
            .Build(items.Select(i => (i.Handle, i.Entry)).ToList(), ReproFormat.Http, false);

        var token = Assert.Single(r.Dependencies!, d => d.Entry == "checkout-flow:entry-3");
        Assert.Equal("checkout-flow:entry-2", token.ProducedBy);
        Assert.Equal("$.accessToken", token.ProducedAt);
        Assert.StartsWith("[REDACTED", token.Value);

        var order = Assert.Single(r.Dependencies!, d => d.Entry == "checkout-flow:entry-8");
        Assert.Equal(("ord-77", "checkout-flow:entry-7", "$.orderId", "body:$.orderId"),
            (order.Value, order.ProducedBy, order.ProducedAt, order.UsedIn));
    }

    [Fact]
    public void Repro_curl_uses_bash_quoting()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var item = t.Store.GetEntry("checkout-flow:entry-6");
        var r = new ReproService(t.Options, t.Redactor, new JsonBodyCache())
            .Build([(item.Handle, item.Entry)], ReproFormat.Curl, false);

        var text = Assert.Single(r.Requests).Text;
        Assert.StartsWith("curl -X POST 'https://api.example.test/api/cart/items'", text);
        Assert.Contains("--data-raw '{\"sku\":\"A1\",\"quantity\":1}'", text);
        Assert.Contains(r.Warnings, w => w.Contains("PowerShell"));
    }

    [Fact]
    public void Repro_limits_entry_count()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var items = Enumerable.Range(1, 11).Select(n => t.Store.GetEntry($"checkout-flow:entry-{n}")).ToList();
        Assert.Throws<HarScopeException>(() => new ReproService(t.Options, t.Redactor, new JsonBodyCache())
            .Build(items.Select(i => (i.Handle, i.Entry)).ToList(), ReproFormat.Http, false));
    }

    // ---------- cache ----------

    [Fact]
    public void Body_cache_reuses_parsed_bodies_and_respects_revision()
    {
        using var t = new TestRoot();
        var h = t.OpenSample();
        var cache = new JsonBodyCache(maxEntries: 2);
        var (_, e8) = t.Store.GetEntry("checkout-flow:entry-8");
        var (_, e7) = t.Store.GetEntry("checkout-flow:entry-7");
        var (_, e6) = t.Store.GetEntry("checkout-flow:entry-6");

        var first = cache.Get(h.Capture, e8, BodySide.Response, 1000);
        Assert.Same(first, cache.Get(h.Capture, e8, BodySide.Response, 1000));

        cache.Get(h.Capture, e7, BodySide.Response, 1000);
        cache.Get(h.Capture, e6, BodySide.Response, 1000);
        Assert.Equal(2, cache.Count);
        Assert.NotSame(first, cache.Get(h.Capture, e8, BodySide.Response, 1000)); // evicted, parsed again
    }
}
