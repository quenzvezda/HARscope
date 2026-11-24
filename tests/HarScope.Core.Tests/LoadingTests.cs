using HarScope.Core.Indexing;

namespace HarScope.Core.Tests;

public class LoadingTests
{
    [Fact]
    public void Loads_valid_har_and_indexes_every_entry()
    {
        using var t = new TestRoot();
        var c = t.OpenSample().Capture;

        Assert.Equal("checkout-flow", c.CaptureId);
        Assert.Equal(13, c.Entries.Count);
        Assert.Equal("1.2", c.HarVersion);
        Assert.Equal("WebInspector 537.36", c.Creator);
        Assert.Equal(12, c.Fingerprint.Revision.Length);
    }

    [Fact]
    public void Entry_ids_are_capture_slug_plus_original_position()
    {
        using var t = new TestRoot();
        var c = t.OpenSample().Capture;

        Assert.Equal("checkout-flow:entry-1", c.Entries[0].Id);
        var payment = c.Entries[7];
        Assert.Equal(8, payment.Number);
        Assert.Equal("checkout-flow:entry-8", payment.Id);
        Assert.Equal("/api/payments", payment.Path);

        var (_, resolved) = t.Store.GetEntry("checkout-flow:entry-8");
        Assert.Same(payment, resolved);
    }

    [Fact]
    public void Entry_identity_survives_eviction_and_reopen()
    {
        using var t = new TestRoot(o => o.MaxOpenCaptures = 1);
        t.Write("other.har", HarText.Of(HarText.Entry("https://x.test/a")));

        var first = t.OpenSample().Capture.Entries[7];
        t.Store.Open("other.har"); // evicts checkout-flow
        var (_, again) = t.Store.GetEntry("checkout-flow:entry-8");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Url, again.Url);
    }

    [Fact]
    public void Parses_entry_metadata_and_tolerates_negative_timings()
    {
        using var t = new TestRoot();
        var e = t.OpenSample().Capture.Entries;

        var failed = e[8];
        Assert.Equal(0, failed.Status);
        Assert.Equal("net::ERR_CONNECTION_RESET", failed.ErrorText);
        Assert.True(failed.IsFailed);

        var redirect = e[9];
        Assert.True(redirect.IsRedirect);
        Assert.Equal("https://shop.example.test/cart", redirect.RedirectUrl);

        Assert.Equal("corr-77", e[7].Correlation["x-correlation-id"]);
        Assert.Equal("application/json", e[1].MimeType);
        Assert.True(e[4].ResponseBase64);
        Assert.False(e[11].HasResponseBody);
        Assert.Equal(120000, e[11].ResponseBodySize);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"foo":{}}""")]
    [InlineData("""{"log":{"version":"1.2"}}""")]
    [InlineData("""{"log":{"entries":{}}}""")]
    public void Rejects_invalid_har_content(string content)
    {
        using var t = new TestRoot();
        t.Write("bad.har", content);

        var ex = Assert.Throws<HarScopeException>(() => t.Store.Open("bad.har"));
        Assert.Equal(ErrorCodes.InvalidHar, ex.Code);
    }

    [Fact]
    public void Accepts_utf8_bom_and_missing_optional_fields()
    {
        using var t = new TestRoot();
        var minimal = """{"log":{"entries":[{"request":{"url":"https://x.test/"}}, 5]}}""";
        File.WriteAllText(Path.Combine(t.Root, "min.har"), minimal, new System.Text.UTF8Encoding(true));

        var c = t.Store.Open("min.har").Capture;

        var only = Assert.Single(c.Entries);
        Assert.Equal(0, only.Status);
        Assert.Null(only.Started);
        Assert.Null(only.TimeMs);
        Assert.Contains(c.Warnings, w => w.Contains("skipped"));
    }

    [Fact]
    public void Rejects_files_above_size_limit()
    {
        using var t = new TestRoot(o => o.MaxFileBytes = 100);
        var ex = Assert.Throws<HarScopeException>(() => t.OpenSample());
        Assert.Equal(ErrorCodes.FileTooLarge, ex.Code);
    }

    [Fact]
    public void Reloads_capture_when_file_changes()
    {
        using var t = new TestRoot();
        var path = t.Write("live.har", HarText.Of(HarText.Entry("https://x.test/a")));
        var before = t.Store.Open("live.har");

        File.WriteAllText(path, HarText.Of(HarText.Entry("https://x.test/a"), HarText.Entry("https://x.test/b")));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var after = t.Store.Get("live");

        Assert.Single(before.Capture.Entries);
        Assert.Equal(2, after.Capture.Entries.Count);
        Assert.Contains(after.Warnings, w => w.Contains("reloaded"));
        Assert.NotEqual(before.Capture.Fingerprint.Revision, after.Capture.Fingerprint.Revision);
    }

    [Fact]
    public void Lists_har_files_and_switches_active_capture()
    {
        using var t = new TestRoot();
        t.Write("team/login.har", HarText.Of(HarText.Entry("https://x.test/login")));
        t.Write("notes.txt", "ignored");

        var files = t.Store.List(out var truncated);
        Assert.False(truncated);
        Assert.Equal(["checkout-flow.har", "team/login.har"], files.Select(f => f.Path).Order().ToArray());

        t.OpenSample();
        t.Store.Open("team/login.har");
        Assert.Equal("team-login", t.Store.Get(null).Capture.CaptureId);
        Assert.Equal("checkout-flow", t.Store.Get("checkout-flow").Capture.CaptureId);
    }

    [Fact]
    public void Slug_collisions_get_a_suffix()
    {
        using var t = new TestRoot();
        t.Write("a b.har", HarText.Of());
        t.Write("a-b.har", HarText.Of());

        var first = t.Store.Open("a b.har").Capture.CaptureId;
        var second = t.Store.Open("a-b.har").Capture.CaptureId;

        Assert.Equal("a-b", first);
        Assert.Equal("a-b-2", second);
    }

    [Theory]
    [InlineData("Checkout Failed 23-11.har", "checkout-failed-23-11")]
    [InlineData("team/Login_OK.HAR", "team-login-ok")]
    [InlineData("!!!.har", "capture")]
    public void Slug_is_derived_from_relative_path(string path, string expected) =>
        Assert.Equal(expected, CaptureStore.Slug(path));

    [Fact]
    public void Unknown_entries_and_captures_are_reported()
    {
        using var t = new TestRoot();
        Assert.Equal(ErrorCodes.NoActiveCapture, Assert.Throws<HarScopeException>(() => t.Store.Get(null)).Code);

        t.OpenSample();
        Assert.Equal(ErrorCodes.UnknownEntry,
            Assert.Throws<HarScopeException>(() => t.Store.GetEntry("checkout-flow:entry-99")).Code);
        Assert.Equal(ErrorCodes.UnknownCapture,
            Assert.Throws<HarScopeException>(() => t.Store.GetEntry("nope:entry-1")).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            Assert.Throws<HarScopeException>(() => t.Store.GetEntry("checkout-flow-8")).Code);
    }
}
