namespace HarScope.Core;

/// <summary>Runtime limits and settings. All values have conservative defaults.</summary>
public sealed class HarScopeOptions
{
    /// <summary>Directory that contains the HAR files. Nothing outside it is readable.</summary>
    public string? RootPath { get; set; }

    /// <summary>HAR files larger than this are rejected (the whole file is held in memory).</summary>
    public long MaxFileBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Number of captures kept in memory at once (least recently used is evicted).</summary>
    public int MaxOpenCaptures { get; set; } = 4;

    /// <summary>Upper bound for the serialized size of a single tool result, in characters.</summary>
    public int MaxOutputChars { get; set; } = 24_000;

    public int DefaultSearchLimit { get; set; } = 20;
    public int MaxSearchLimit { get; set; } = 100;

    public int DefaultBodyChars { get; set; } = 4_000;
    public int MaxBodyChars { get; set; } = 20_000;

    /// <summary>Individual header / cookie / query values longer than this are shortened.</summary>
    public int MaxValueChars { get; set; } = 1_000;

    /// <summary>Maximum number of files returned by har_list.</summary>
    public int MaxListedFiles { get; set; } = 500;

    public RedactionOptions Redaction { get; set; } = new();
}

public sealed class RedactionOptions
{
    /// <summary>When false, header/cookie/query values are returned as recorded.</summary>
    public bool Enabled { get; set; } = true;

    public HashSet<string> HeaderNames { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "apikey",
        "x-auth-token", "x-access-token", "x-refresh-token", "x-csrf-token", "x-xsrf-token", "csrf-token",
        "x-amz-security-token", "x-goog-api-key", "ocp-apim-subscription-key", "x-session-id",
    };

    /// <summary>Header names containing any of these fragments are redacted too.</summary>
    public List<string> HeaderNameFragments { get; } = ["token", "secret", "password", "api-key", "apikey", "session"];

    public HashSet<string> QueryParamNames { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "refresh_token", "id_token", "token", "api_key", "apikey", "api-key", "key",
        "password", "pwd", "passwd", "secret", "client_secret", "sig", "signature", "code", "session",
        "sessionid", "session_id", "auth", "x-amz-security-token", "x-amz-signature", "x-amz-credential",
    };
}
