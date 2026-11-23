using HarScope.Core.Querying;
using HarScope.Core.Security;

namespace HarScope.Core.Tests;

public class RedactionTests
{
    [Fact]
    public void Sensitive_headers_and_cookies_are_redacted_with_stable_fingerprints()
    {
        using var t = new TestRoot();
        t.OpenSample();

        EntryDetails Get(int n, params string[] sections)
        {
            var (h, e) = t.Store.GetEntry($"checkout-flow:entry-{n}");
            return t.Inspector.Inspect(h, e, sections);
        }

        var profile = Get(3, "headers", "cookies");
        var products = Get(4, "headers");
        var login = Get(2, "headers", "cookies");
        var json = HarJson.Serialize(profile) + HarJson.Serialize(login);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.", json);
        Assert.DoesNotContain("s3ss10n-abc", json);

        var auth1 = profile.RequestHeaders!.Single(h => h.Name == "authorization").Value;
        var auth2 = products.RequestHeaders!.Single(h => h.Name == "authorization").Value;
        Assert.StartsWith("[REDACTED len=", auth1);
        Assert.Equal(auth1, auth2); // same token -> same fingerprint

        Assert.StartsWith("[REDACTED", login.ResponseHeaders!.Single(h => h.Name == "set-cookie").Value);
        Assert.Equal("session", login.Cookies!.Response[0].Name);
        Assert.StartsWith("[REDACTED", login.Cookies.Response[0].Value);
        Assert.Equal("application/json", login.ResponseHeaders!.Single(h => h.Name == "content-type").Value);
    }

    [Fact]
    public void Bodies_are_not_redacted()
    {
        using var t = new TestRoot();
        t.OpenSample();
        var (h, e) = t.Store.GetEntry("checkout-flow:entry-2");
        var d = t.Inspector.Inspect(h, e, ["requestBody"]);
        Assert.Contains("hunter2", d.RequestBody!.Text);
    }

    [Fact]
    public void Redaction_can_be_disabled()
    {
        using var t = new TestRoot(o => o.Redaction.Enabled = false);
        t.OpenSample();
        var (h, e) = t.Store.GetEntry("checkout-flow:entry-3");
        var d = t.Inspector.Inspect(h, e, ["requestHeaders"]);
        Assert.StartsWith("Bearer eyJ", d.RequestHeaders!.Single(x => x.Name == "authorization").Value);
    }

    [Theory]
    [InlineData("https://x.test/cb?code=abc123&state=ok", "state=ok", "abc123")]
    [InlineData("/a?access_token=zzz#frag", "#frag", "zzz")]
    [InlineData("/a?q=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI0MiJ9.sig", "q=[REDACTED", "eyJhbGci")]
    public void Urls_keep_structure_but_hide_secrets(string url, string kept, string hidden)
    {
        var r = new Redactor(new RedactionOptions());
        var result = r.Url(url);
        Assert.Contains(kept, result);
        Assert.DoesNotContain(hidden, result);
    }

    [Fact]
    public void Custom_header_names_are_honoured()
    {
        var options = new RedactionOptions();
        options.HeaderNames.Add("x-tenant-secret-ish");
        var r = new Redactor(options);
        Assert.StartsWith("[REDACTED", r.HeaderValue("X-Tenant-Secret-Ish", "v"));
        Assert.Equal("text/html", r.HeaderValue("accept", "text/html"));
    }
}
