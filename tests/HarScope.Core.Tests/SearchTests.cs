using HarScope.Core.Querying;

namespace HarScope.Core.Tests;

public class SearchTests
{
    private static SearchResult Run(TestRoot t, SearchQuery q) => t.Search.Search(t.OpenSample(), q);

    [Fact]
    public void Default_order_is_chronological_by_start_time()
    {
        using var t = new TestRoot();
        var r = Run(t, new SearchQuery { Limit = 100 });

        var numbers = r.Entries.Select(e => int.Parse(e.Id.Split("entry-")[1])).ToArray();
        Assert.Equal([1, 12, 10, 2, 3, 4, 5, 11, 6, 7, 8, 13, 9], numbers);
        Assert.Equal(13, r.TotalMatches);
        Assert.False(r.HasMore);
    }

    [Fact]
    public void Filters_by_method_status_and_url()
    {
        using var t = new TestRoot();

        Assert.Equal(5, Run(t, new SearchQuery { Method = "post" }).TotalMatches);
        Assert.Equal("checkout-flow:entry-8", Assert.Single(Run(t, new SearchQuery { Status = 502 }).Entries).Id);
        Assert.Equal(2, Run(t, new SearchQuery { UrlContains = "/api/payments" }).TotalMatches);
        Assert.Equal(2, Run(t, new SearchQuery { StatusMin = 300, StatusMax = 599 }).TotalMatches);
        Assert.Equal(3, Run(t, new SearchQuery { Host = "shop.example" }).TotalMatches);
        Assert.Single(Run(t, new SearchQuery { Status = 0 }).Entries);
    }

    [Fact]
    public void Failed_only_includes_http_errors_and_network_failures()
    {
        using var t = new TestRoot();
        var r = Run(t, new SearchQuery { FailedOnly = true });

        Assert.Equal(["checkout-flow:entry-8", "checkout-flow:entry-9"], r.Entries.Select(e => e.Id).ToArray());
        Assert.Contains("netError:net::ERR_CONNECTION_RESET", r.Entries[1].Flags!);
    }

    [Fact]
    public void Filters_by_duration_time_range_headers_and_correlation()
    {
        using var t = new TestRoot();

        Assert.Equal("checkout-flow:entry-8", Assert.Single(Run(t, new SearchQuery { MinDurationMs = 1000 }).Entries).Id);
        Assert.Equal(2, Run(t, new SearchQuery { CorrelationId = "corr-77" }).TotalMatches);
        Assert.Equal(2, Run(t, new SearchQuery { RequestHeader = "Authorization" }).TotalMatches);
        Assert.Equal(1, Run(t, new SearchQuery { ResponseHeader = "set-cookie" }).TotalMatches);
        Assert.Equal(3, Run(t, new SearchQuery
        {
            From = DateTimeOffset.Parse("2026-11-20T09:00:11Z"),
            To = DateTimeOffset.Parse("2026-11-20T09:00:13Z"),
        }).TotalMatches);
        Assert.Equal(1, Run(t, new SearchQuery { ResourceType = "xhr", MimeType = "json" }).TotalMatches);
    }

    [Fact]
    public void Filters_by_body_text()
    {
        using var t = new TestRoot();

        Assert.Equal(["checkout-flow:entry-7", "checkout-flow:entry-8", "checkout-flow:entry-13"],
            Run(t, new SearchQuery { BodyContains = "ord-77" }).Entries.Select(e => e.Id).ToArray());
        Assert.Equal(["checkout-flow:entry-7"],
            Run(t, new SearchQuery { BodyContains = "ord-77", BodySide = BodySide.Response }).Entries.Select(e => e.Id).ToArray());
        Assert.Equal(["checkout-flow:entry-8", "checkout-flow:entry-13"],
            Run(t, new SearchQuery { BodyContains = "ord-77", BodySide = BodySide.Request }).Entries.Select(e => e.Id).ToArray());
        Assert.Equal("checkout-flow:entry-8",
            Assert.Single(Run(t, new SearchQuery { BodyContains = "gateway_timeout" }).Entries).Id);
        // base64-encoded JSON response is decoded before matching
        Assert.Equal("checkout-flow:entry-11",
            Assert.Single(Run(t, new SearchQuery { BodyContains = "\"sku\":\"B2\",\"score\"" }).Entries).Id);
        // form params (no postData.text)
        Assert.Equal("checkout-flow:entry-13",
            Assert.Single(Run(t, new SearchQuery { BodyContains = "payment_failed" }).Entries).Id);
    }

    [Fact]
    public void Data_urls_are_hidden_unless_requested()
    {
        using var t = new TestRoot();
        t.Write("data.har", HarText.Of(
            HarText.Entry("https://x.test/api"),
            HarText.Entry("data:image/png;base64,iVBORw0KGgo=")));
        var h = t.Store.Open("data.har");

        var hidden = t.Search.Search(h, new SearchQuery());
        Assert.Equal(1, hidden.TotalMatches);
        Assert.Contains(hidden.Warnings!, w => w.Contains("data: URL"));
        Assert.Equal(2, t.Search.Search(h, new SearchQuery { IncludeDataUrls = true }).TotalMatches);
    }

    [Fact]
    public void Paginates_with_offset_and_next_offset()
    {
        using var t = new TestRoot();
        var page1 = Run(t, new SearchQuery { Limit = 5 });
        var page3 = Run(t, new SearchQuery { Limit = 5, Offset = 10 });

        Assert.Equal(5, page1.ReturnedCount);
        Assert.True(page1.HasMore);
        Assert.Equal(5, page1.NextOffset);
        Assert.Equal(3, page3.ReturnedCount);
        Assert.False(page3.HasMore);
        Assert.Null(page3.NextOffset);
    }

    [Fact]
    public void Sorts_by_duration_descending()
    {
        using var t = new TestRoot();
        var r = Run(t, new SearchQuery { Sort = SearchSort.Duration, Descending = true, Limit = 3 });
        Assert.Equal(["checkout-flow:entry-8", "checkout-flow:entry-7", "checkout-flow:entry-4"], r.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Limit_is_capped_at_the_maximum()
    {
        using var t = new TestRoot(o => o.MaxSearchLimit = 4);
        var r = Run(t, new SearchQuery { Limit = 50 });
        Assert.Equal(4, r.ReturnedCount);
        Assert.Contains(r.Warnings!, w => w.Contains("exceeds the maximum"));
    }

    [Fact]
    public void Page_is_shortened_to_respect_output_limit()
    {
        using var t = new TestRoot(o => o.MaxOutputChars = 2_000);
        var r = Run(t, new SearchQuery { Limit = 100 });

        Assert.True(r.Truncated);
        Assert.True(r.HasMore);
        Assert.Equal(r.ReturnedCount, r.NextOffset);
        Assert.True(HarJson.Serialize(r).Length <= 2_000);
    }

    [Fact]
    public void Rows_redact_secret_query_values()
    {
        using var t = new TestRoot();
        var row = Assert.Single(Run(t, new SearchQuery { PathContains = "/api/products" }).Entries);

        Assert.Contains("q=shoes", row.Path);
        Assert.DoesNotContain("k-123456", row.Path);
        Assert.Contains("api_key=[REDACTED", row.Path);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        using var t = new TestRoot();
        Assert.Throws<HarScopeException>(() => Run(t, new SearchQuery { Offset = -1 }));
        Assert.Throws<HarScopeException>(() => Run(t, new SearchQuery { Limit = 0 }));
        Assert.Throws<HarScopeException>(() => Run(t, new SearchQuery { StatusMin = 500, StatusMax = 400 }));
    }
}
