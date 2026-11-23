using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HarScope.Core.Security;

/// <summary>
/// Redacts secret-bearing header, cookie and query-string values before they leave the server.
/// Redacted values keep their length and a short fingerprint (HMAC with a per-process random key),
/// so an agent can still tell whether two requests carried the same token without seeing it.
/// Bodies are not redacted.
/// </summary>
public sealed partial class Redactor
{
    private readonly RedactionOptions _options;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public Redactor(RedactionOptions options) => _options = options;

    public bool Enabled => _options.Enabled;

    public string Mask(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return $"[REDACTED len={value.Length} fp={Convert.ToHexStringLower(mac, 0, 4)}]";
    }

    public string HeaderValue(string name, string value)
    {
        if (!Enabled || value.Length == 0) return value;
        return IsSensitiveHeader(name) || LooksLikeSecret(value) ? Mask(value) : value;
    }

    public string QueryValue(string name, string value)
    {
        if (!Enabled || value.Length == 0) return value;
        return _options.QueryParamNames.Contains(name) || LooksLikeSecret(value) ? Mask(value) : value;
    }

    /// <summary>Cookie values are always secret-like; names are kept.</summary>
    public string CookieValue(string value) => !Enabled || value.Length == 0 ? value : Mask(value);

    /// <summary>Redacts sensitive query-string values inside a URL or path, keeping everything else.</summary>
    public string Url(string url)
    {
        if (!Enabled) return url;
        var q = url.IndexOf('?');
        if (q < 0) return url;

        var hash = url.IndexOf('#', q);
        var query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        var fragment = hash < 0 ? "" : url[hash..];

        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0) continue;
            var name = SafeUnescape(parts[i][..eq]);
            var value = SafeUnescape(parts[i][(eq + 1)..]);
            var redacted = QueryValue(name, value);
            if (redacted != value)
                parts[i] = parts[i][..(eq + 1)] + redacted;
        }

        return url[..(q + 1)] + string.Join('&', parts) + fragment;
    }

    private bool IsSensitiveHeader(string name) =>
        _options.HeaderNames.Contains(name) ||
        _options.HeaderNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeSecret(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
        JwtPattern().IsMatch(value);

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtPattern();
}
