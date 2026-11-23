using System.Globalization;
using System.Text.Json;
using HarScope.Core.Models;

namespace HarScope.Core.Parsing;

/// <summary>
/// Validates HAR 1.2 structure and builds the metadata index. Tolerant of missing optional
/// fields, negative timing sentinels and browser extension fields (prefixed with "_").
/// </summary>
public static class HarParser
{
    public static readonly HashSet<string> CorrelationHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-request-id", "x-correlation-id", "correlation-id", "request-id", "x-trace-id", "traceparent",
        "x-amzn-trace-id", "x-amzn-requestid", "x-b3-traceid", "x-cloud-trace-context", "x-ms-request-id",
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    public static HarCapture Parse(ReadOnlyMemory<byte> utf8, string captureId, string relativePath, string fullPath,
        FileFingerprint fingerprint)
    {
        // Skip a UTF-8 BOM if present.
        if (utf8.Length >= 3 && utf8.Span[0] == 0xEF && utf8.Span[1] == 0xBB && utf8.Span[2] == 0xBF)
            utf8 = utf8[3..];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new HarScopeException(ErrorCodes.InvalidHar,
                $"File is not valid JSON (line {ex.LineNumber + 1}, byte {ex.BytePositionInLine}).");
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("log", out var log) || log.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new HarScopeException(ErrorCodes.InvalidHar, "Not a HAR file: missing top-level \"log\" object.");
        }

        if (!log.TryGetProperty("entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
        {
            doc.Dispose();
            throw new HarScopeException(ErrorCodes.InvalidHar, "Not a HAR file: missing \"log.entries\" array.");
        }

        var warnings = new List<string>();
        var version = Json.String(log, "version");
        if (version is not null && version != "1.2" && version != "1.1")
            warnings.Add($"HAR version {version} is not 1.2; parsing on a best-effort basis.");

        var entries = new List<HarEntry>(entriesElement.GetArrayLength());
        var number = 0;
        var skipped = 0;
        foreach (var raw in entriesElement.EnumerateArray())
        {
            number++;
            if (raw.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }
            entries.Add(ParseEntry(raw, number, captureId));
        }

        if (skipped > 0) warnings.Add($"{skipped} entries were not JSON objects and were skipped.");

        var noStart = entries.Count(e => e.Started is null);
        if (noStart > 0) warnings.Add($"{noStart} entries have no valid startedDateTime; they sort last.");

        string? creator = null, browser = null;
        if (log.TryGetProperty("creator", out var c) && c.ValueKind == JsonValueKind.Object)
            creator = Join(Json.String(c, "name"), Json.String(c, "version"));
        if (log.TryGetProperty("browser", out var b) && b.ValueKind == JsonValueKind.Object)
            browser = Join(Json.String(b, "name"), Json.String(b, "version"));

        return new HarCapture
        {
            CaptureId = captureId,
            RelativePath = relativePath,
            FullPath = fullPath,
            Fingerprint = fingerprint,
            LoadedAt = DateTimeOffset.UtcNow,
            HarVersion = version,
            Creator = creator,
            Browser = browser,
            Entries = entries,
            Warnings = warnings,
            Document = doc,
        };
    }

    private static HarEntry ParseEntry(JsonElement raw, int number, string captureId)
    {
        var request = Json.Object(raw, "request");
        var response = Json.Object(raw, "response");
        var content = response is { } r ? Json.Object(r, "content") : null;
        var postData = request is { } q ? Json.Object(q, "postData") : null;

        var url = request is { } req ? Json.String(req, "url") ?? "" : "";
        var (host, path, query) = SplitUrl(url);

        DateTimeOffset? started = null;
        if (Json.String(raw, "startedDateTime") is { } s &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            started = dt;

        var time = Json.Number(raw, "time");
        var mime = content is { } ct ? NormalizeMime(Json.String(ct, "mimeType")) : null;

        string? errorText = null;
        if (response is { } resp)
            errorText = Json.String(resp, "_error") ?? Json.String(resp, "_errorText");
        errorText ??= Json.String(raw, "_error");
        if (string.IsNullOrEmpty(errorText)) errorText = null;

        var correlation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectCorrelation(request, correlation);
        CollectCorrelation(response, correlation);

        var postText = postData is { } pd ? Json.String(pd, "text") : null;
        var hasPostParams = postData is { } pd2 && pd2.TryGetProperty("params", out var pp) &&
                            pp.ValueKind == JsonValueKind.Array && pp.GetArrayLength() > 0;
        var requestBodySize = request is { } rq2 ? Json.Number(rq2, "bodySize") : null;

        var responseText = content is { } ct2 ? Json.String(ct2, "text") : null;

        return new HarEntry
        {
            Id = $"{captureId}:entry-{number}",
            Number = number,
            Started = started,
            Method = (request is { } rq ? Json.String(rq, "method") : null)?.ToUpperInvariant() ?? "",
            Url = url,
            Host = host,
            Path = path,
            Query = query,
            Status = response is { } rs && Json.Number(rs, "status") is { } st ? (int)st : 0,
            StatusText = response is { } rs2 ? NullIfEmpty(Json.String(rs2, "statusText")) : null,
            TimeMs = time is >= 0 ? time : null,
            MimeType = mime,
            ResourceType = Json.String(raw, "_resourceType"),
            ErrorText = errorText,
            RedirectUrl = response is { } rs3 ? NullIfEmpty(Json.String(rs3, "redirectURL")) : null,
            HasRequestBody = !string.IsNullOrEmpty(postText) || hasPostParams,
            RequestBodySize = postText is not null ? postText.Length : requestBodySize is > 0 ? (long)requestBodySize.Value : null,
            HasResponseBody = !string.IsNullOrEmpty(responseText),
            ResponseBodySize = content is { } ct3 && Json.Number(ct3, "size") is { } size && size >= 0 ? (long)size : null,
            ResponseBase64 = content is { } ct4 &&
                             string.Equals(Json.String(ct4, "encoding"), "base64", StringComparison.OrdinalIgnoreCase),
            Correlation = correlation,
            Raw = raw,
        };
    }

    private static void CollectCorrelation(JsonElement? message, Dictionary<string, string> target)
    {
        if (message is not { } m || !m.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array) return;

        foreach (var h in headers.EnumerateArray())
        {
            if (Json.String(h, "name") is { } name && CorrelationHeaderNames.Contains(name) &&
                Json.String(h, "value") is { Length: > 0 } value)
                target.TryAdd(name.ToLowerInvariant(), value);
        }
    }

    internal static (string Host, string Path, string? Query) SplitUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ws" or "wss")
        {
            var query = uri.Query.Length > 1 ? uri.Query[1..] : null;
            var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return (host, uri.AbsolutePath, query);
        }

        // data:, blob:, chrome-extension: and malformed URLs: keep something readable.
        var schemeEnd = url.IndexOf(':');
        var scheme = schemeEnd > 0 ? url[..schemeEnd] : "";
        return (scheme, url.Length > 120 ? url[..120] + "…" : url, null);
    }

    private static string? NormalizeMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var semi = mime.IndexOf(';');
        return (semi >= 0 ? mime[..semi] : mime).Trim().ToLowerInvariant();
    }

    private static string? Join(string? a, string? b) =>
        a is null ? null : b is null ? a : $"{a} {b}";

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Small, null-tolerant accessors for HAR JSON.</summary>
internal static class Json
{
    public static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetDouble(out var d)
            ? d
            : null;

    public static JsonElement? Object(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static IEnumerable<JsonElement> Array(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];
}
