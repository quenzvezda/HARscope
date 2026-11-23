using HarScope.Core.Querying;

namespace HarScope.Core.Tests;

public class InspectionTests
{
    private static EntryDetails Inspect(TestRoot t, int number, string[]? sections, int offset = 0, int? max = null)
    {
        t.OpenSample();
        var (handle, entry) = t.Store.GetEntry($"checkout-flow:entry-{number}");
        return t.Inspector.Inspect(handle, entry, sections, offset, max);
    }

    [Fact]
    public void Summary_only_by_default()
    {
        using var t = new TestRoot();
        var d = Inspect(t, 8, null);

        Assert.Equal(502, d.Summary.Status);
        Assert.Null(d.RequestHeaders);
        Assert.Null(d.ResponseBody);
        Assert.Null(d.RequestBody);
        Assert.Null(d.Timings);
        Assert.DoesNotContain("GATEWAY_TIMEOUT", HarJson.Serialize(d));
    }

    [Fact]
    public void Returns_requested_json_bodies()
    {
        using var t = new TestRoot();
        var d = Inspect(t, 8, ["requestBody", "responseBody"]);

        Assert.Contains("\"orderId\":\"ord-77\"", d.RequestBody!.Text);
        Assert.Contains("GATEWAY_TIMEOUT", d.ResponseBody!.Text);
        Assert.False(d.ResponseBody.Truncated);
        Assert.Equal("application/json", d.ResponseBody.MimeType);
    }

    [Fact]
    public void Bodies_are_chunked_and_can_be_paged()
    {
        using var t = new TestRoot();
        var first = Inspect(t, 8, ["responseBody"], 0, 20).ResponseBody!;
        var second = Inspect(t, 8, ["responseBody"], first.NextOffset!.Value, 1000).ResponseBody!;

        Assert.True(first.Truncated);
        Assert.Equal(20, first.ReturnedChars);
        Assert.Equal(20, first.NextOffset);
        Assert.Equal(first.TotalChars, first.Text!.Length + second.Text!.Length);
        Assert.Null(second.NextOffset);
    }

    [Fact]
    public void Body_size_is_bounded_by_output_limit()
    {
        using var t = new TestRoot(o => o.MaxOutputChars = 6_600);
        var d = Inspect(t, 1, ["responseBody"], 0, 50_000);
        Assert.Contains(d.Warnings!, w => w.Contains("bodyMaxChars reduced"));
    }

    [Fact]
    public void Decodes_base64_text_and_withholds_binary()
    {
        using var t = new TestRoot();

        var json = Inspect(t, 11, ["responseBody"]).ResponseBody!;
        Assert.Equal("base64", json.SourceEncoding);
        Assert.StartsWith("{\"items\":[{\"sku\":\"A1\"", json.Text);

        var png = Inspect(t, 5, ["responseBody"]).ResponseBody!;
        Assert.True(png.Available);
        Assert.True(png.Binary);
        Assert.Null(png.Text);
        Assert.Equal("image/png", png.MimeType);
        Assert.True(png.SizeBytes > 0);
    }

    [Fact]
    public void Missing_bodies_are_reported_not_invented()
    {
        using var t = new TestRoot();

        var omitted = Inspect(t, 12, ["responseBody", "requestBody"]);
        Assert.False(omitted.ResponseBody!.Available);
        Assert.Contains("not captured", omitted.ResponseBody.Reason);
        Assert.Contains("Content omitted by exporter", omitted.ResponseBody.Reason);
        Assert.False(omitted.RequestBody!.Available);

        var failed = Inspect(t, 9, ["responseBody"]);
        Assert.False(failed.ResponseBody!.Available);
        Assert.Contains("No response", failed.ResponseBody.Reason);
    }

    [Fact]
    public void Form_params_are_returned_when_post_text_is_absent()
    {
        using var t = new TestRoot();
        var body = Inspect(t, 13, ["requestBody"]).RequestBody!;
        Assert.True(body.Available);
        Assert.Equal(["event", "orderId"], body.Params!.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Timings_map_negative_sentinels_to_null()
    {
        using var t = new TestRoot();
        var timings = Inspect(t, 2, ["timings"]).Timings!;

        Assert.Null(timings["dns"]);
        Assert.Null(timings["ssl"]);
        Assert.Equal(240.2, timings["wait"]);
        Assert.Equal(245.0, timings["total"]);
    }

    [Fact]
    public void Query_params_fall_back_to_url_and_are_redacted()
    {
        using var t = new TestRoot();
        var q = Inspect(t, 9, ["queryParams"]).QueryParams!;

        Assert.Equal("ord-77", q.Single(p => p.Name == "orderId").Value);
        Assert.StartsWith("[REDACTED len=9", q.Single(p => p.Name == "token").Value);
    }

    [Fact]
    public void Unknown_section_is_rejected()
    {
        using var t = new TestRoot();
        var ex = Assert.Throws<HarScopeException>(() => Inspect(t, 1, ["everything"]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Overview_contains_statistics_but_no_bodies()
    {
        using var t = new TestRoot();
        var o = t.Overview.Build(t.OpenSample());
        var json = HarJson.Serialize(o);

        Assert.Equal(13, o.TotalEntries);
        Assert.Equal(2, o.FailedCount);
        Assert.Equal(5, o.Methods["POST"]);
        Assert.Equal(1, o.StatusClasses["5xx"]);
        Assert.Equal(1, o.StatusClasses["noResponse"]);
        Assert.Equal("checkout-flow:entry-8", o.Slowest[0].Id);
        Assert.Equal(43_012, o.DurationMs);
        Assert.DoesNotContain("GATEWAY_TIMEOUT", json);
        Assert.DoesNotContain("hunter2", json);
    }
}
