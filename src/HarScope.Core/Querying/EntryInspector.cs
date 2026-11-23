using System.Text;
using System.Text.Json;
using HarScope.Core.Indexing;
using HarScope.Core.Models;
using HarScope.Core.Parsing;
using HarScope.Core.Security;

namespace HarScope.Core.Querying;

public static class EntrySections
{
    public const string RequestHeaders = "requestHeaders";
    public const string QueryParams = "queryParams";
    public const string RequestBody = "requestBody";
    public const string ResponseHeaders = "responseHeaders";
    public const string ResponseBody = "responseBody";
    public const string Timings = "timings";
    public const string Cookies = "cookies";

    public static readonly IReadOnlyList<string> All =
        [RequestHeaders, QueryParams, RequestBody, ResponseHeaders, ResponseBody, Timings, Cookies];
}

public sealed record NameValue(string Name, string Value);

public sealed record CookieInfo(string Name, string Value, string? Domain = null, string? Path = null, string? Expires = null,
    bool? HttpOnly = null, bool? Secure = null);

public sealed record CookieSection(IReadOnlyList<CookieInfo> Request, IReadOnlyList<CookieInfo> Response);

public sealed record FormParam(string Name, string? Value, string? FileName, string? ContentType);

/// <summary>Bounded slice of a request or response body.</summary>
public sealed record BodyChunk
{
    public required bool Available { get; init; }
    public string? Reason { get; init; }
    public string? MimeType { get; init; }
    /// <summary>"base64" when the HAR stored the content Base64-encoded (it is decoded before returning).</summary>
    public string? SourceEncoding { get; init; }
    public bool Binary { get; init; }
    public long? SizeBytes { get; init; }
    public int? TotalChars { get; init; }
    public int? Offset { get; init; }
    public int? ReturnedChars { get; init; }
    public bool Truncated { get; init; }
    public int? NextOffset { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<FormParam>? Params { get; init; }
}

public sealed record EntrySummary(
    int Number,
    DateTimeOffset? Started,
    string Method,
    string Url,
    string? HttpVersion,
    int Status,
    string? StatusText,
    double? TimeMs,
    string? MimeType,
    string? ResourceType,
    string? ServerIp,
    string? Error,
    string? RedirectUrl,
    IReadOnlyList<string>? Flags,
    IReadOnlyDictionary<string, string>? Correlation,
    IReadOnlyDictionary<string, object?> Sizes);

public sealed record EntryDetails
{
    public required string Id { get; init; }
    public required string CaptureId { get; init; }
    public required string Revision { get; init; }
    public required EntrySummary Summary { get; init; }
    public required IReadOnlyList<string> AvailableSections { get; init; }
    public IReadOnlyList<NameValue>? RequestHeaders { get; init; }
    public IReadOnlyList<NameValue>? QueryParams { get; init; }
    public BodyChunk? RequestBody { get; init; }
    public IReadOnlyList<NameValue>? ResponseHeaders { get; init; }
    public BodyChunk? ResponseBody { get; init; }
    public IReadOnlyDictionary<string, double?>? Timings { get; init; }
    public CookieSection? Cookies { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Level 3: one entry, only the sections the caller asks for.</summary>
public sealed class EntryInspector(HarScopeOptions options, Redactor redactor)
{
    /// <summary>Characters reserved for everything except bodies when sizing body chunks.</summary>
    private const int NonBodyReserve = 6_000;

    public EntryDetails Inspect(CaptureHandle handle, HarEntry entry, IReadOnlyCollection<string>? sections,
        int bodyOffset = 0, int? bodyMaxChars = null)
    {
        var wanted = NormalizeSections(sections);
        if (bodyOffset < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "bodyOffset must be >= 0.");
        if (bodyMaxChars is < 1) throw new HarScopeException(ErrorCodes.InvalidArgument, "bodyMaxChars must be >= 1.");

        var warnings = new List<string>(handle.Warnings);
        var truncated = false;
        var request = Json.Object(entry.Raw, "request");
        var response = Json.Object(entry.Raw, "response");

        // Split the body budget between the requested bodies.
        var bodies = (wanted.Contains(EntrySections.RequestBody) ? 1 : 0) + (wanted.Contains(EntrySections.ResponseBody) ? 1 : 0);
        var perBody = Math.Min(bodyMaxChars ?? options.DefaultBodyChars, options.MaxBodyChars);
        if (bodies > 0)
        {
            var budget = Math.Max(500, (options.MaxOutputChars - NonBodyReserve) / bodies);
            if (perBody > budget)
            {
                warnings.Add($"bodyMaxChars reduced to {budget} to stay within the output limit.");
                perBody = budget;
            }
        }

        IReadOnlyList<NameValue>? Headers(JsonElement? message, bool isRequest)
        {
            var list = Json.Array(message, "headers")
                .Select(h => (Name: Json.String(h, "name") ?? "", Value: Json.String(h, "value") ?? ""))
                .Where(h => h.Name.Length > 0 && !h.Name.StartsWith(':')) // drop HTTP/2 pseudo-headers duplicates
                .Select(h => new NameValue(h.Name, Shorten(redactor.HeaderValue(h.Name, h.Value), ref truncated)))
                .ToList();
            if (list.Count == 0 && message is not null)
                warnings.Add($"No {(isRequest ? "request" : "response")} headers recorded.");
            return list;
        }

        return new EntryDetails
        {
            Id = entry.Id,
            CaptureId = handle.Capture.CaptureId,
            Revision = handle.Capture.Fingerprint.Revision,
            Summary = Summary(entry, request, response),
            AvailableSections = EntrySections.All,
            RequestHeaders = wanted.Contains(EntrySections.RequestHeaders) ? Headers(request, true) : null,
            ResponseHeaders = wanted.Contains(EntrySections.ResponseHeaders) ? Headers(response, false) : null,
            QueryParams = wanted.Contains(EntrySections.QueryParams) ? QueryParams(request, entry, ref truncated) : null,
            RequestBody = wanted.Contains(EntrySections.RequestBody) ? Body(entry, BodySide.Request, bodyOffset, perBody, warnings) : null,
            ResponseBody = wanted.Contains(EntrySections.ResponseBody) ? Body(entry, BodySide.Response, bodyOffset, perBody, warnings) : null,
            Timings = wanted.Contains(EntrySections.Timings) ? Timings(entry) : null,
            Cookies = wanted.Contains(EntrySections.Cookies) ? Cookies(request, response) : null,
            Truncated = truncated,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static HashSet<string> NormalizeSections(IReadOnlyCollection<string>? sections)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in sections ?? [])
        {
            var s = raw.Trim();
            if (s.Equals("headers", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(EntrySections.RequestHeaders);
                result.Add(EntrySections.ResponseHeaders);
                continue;
            }
            var match = EntrySections.All.FirstOrDefault(a => a.Equals(s, StringComparison.OrdinalIgnoreCase))
                        ?? throw new HarScopeException(ErrorCodes.InvalidArgument,
                            $"Unknown section '{s}'. Valid: {string.Join(", ", EntrySections.All)}, headers.");
            result.Add(match);
        }
        return result;
    }

    private EntrySummary Summary(HarEntry e, JsonElement? request, JsonElement? response)
    {
        var sizes = new Dictionary<string, object?>
        {
            ["requestHeaders"] = Json.Array(request, "headers").Count(),
            ["queryParams"] = Json.Array(request, "queryString").Count(),
            ["requestBodyChars"] = e.RequestBodySize,
            ["responseHeaders"] = Json.Array(response, "headers").Count(),
            ["responseBodyBytes"] = e.ResponseBodySize,
            ["responseBodyCaptured"] = e.HasResponseBody,
            ["requestCookies"] = Json.Array(request, "cookies").Count(),
            ["responseCookies"] = Json.Array(response, "cookies").Count(),
        };
        foreach (var key in sizes.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) sizes.Remove(key);

        var correlation = e.Correlation.Count == 0
            ? null
            : e.Correlation.ToDictionary(kv => kv.Key, kv => redactor.HeaderValue(kv.Key, kv.Value));

        return new EntrySummary(
            e.Number, e.Started, e.Method, redactor.Url(e.Url),
            request is { } rq ? Json.String(rq, "httpVersion") : null,
            e.Status, e.StatusText, e.TimeMs, e.MimeType, e.ResourceType,
            Json.String(e.Raw, "serverIPAddress"), e.ErrorText, e.RedirectUrl is { } r ? redactor.Url(r) : null,
            EntryRow.FlagsOf(e), correlation, sizes);
    }

    private IReadOnlyList<NameValue> QueryParams(JsonElement? request, HarEntry entry, ref bool truncated)
    {
        var list = new List<NameValue>();
        foreach (var q in Json.Array(request, "queryString"))
        {
            var name = Json.String(q, "name") ?? "";
            list.Add(new NameValue(name, Shorten(redactor.QueryValue(name, Json.String(q, "value") ?? ""), ref truncated)));
        }

        // Some exporters leave queryString empty; fall back to parsing the URL.
        if (list.Count == 0 && entry.Query is { } query)
        {
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var name = Unescape(eq < 0 ? part : part[..eq]);
                var value = eq < 0 ? "" : Unescape(part[(eq + 1)..]);
                list.Add(new NameValue(name, Shorten(redactor.QueryValue(name, value), ref truncated)));
            }
        }
        return list;
    }

    private CookieSection Cookies(JsonElement? request, JsonElement? response)
    {
        CookieInfo Map(JsonElement c, bool detailed) => new(
            Json.String(c, "name") ?? "",
            redactor.CookieValue(Json.String(c, "value") ?? ""),
            detailed ? Json.String(c, "domain") : null,
            detailed ? Json.String(c, "path") : null,
            detailed ? Json.String(c, "expires") : null,
            detailed && c.TryGetProperty("httpOnly", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False ? h.GetBoolean() : null,
            detailed && c.TryGetProperty("secure", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False ? s.GetBoolean() : null);

        return new CookieSection(
            Json.Array(request, "cookies").Select(c => Map(c, false)).ToList(),
            Json.Array(response, "cookies").Select(c => Map(c, true)).ToList());
    }

    private static IReadOnlyDictionary<string, double?> Timings(HarEntry e)
    {
        var t = Json.Object(e.Raw, "timings");
        var result = new Dictionary<string, double?>();
        foreach (var phase in new[] { "blocked", "dns", "connect", "ssl", "send", "wait", "receive" })
        {
            // HAR uses -1 for "does not apply"; report it as null rather than a negative duration.
            var v = t is { } tt ? Json.Number(tt, phase) : null;
            result[phase] = v is >= 0 ? Math.Round(v.Value, 3) : null;
        }
        result["total"] = e.TimeMs is { } total ? Math.Round(total, 3) : null;
        return result;
    }

    private BodyChunk Body(HarEntry entry, BodySide side, int offset, int maxChars, List<string> warnings)
    {
        var body = BodyReader.Read(entry, side, options.MaxValueChars);
        if (body.Text is null)
        {
            return new BodyChunk
            {
                Available = body.Available, Reason = body.Reason, MimeType = body.MimeType, SourceEncoding = body.SourceEncoding,
                Binary = body.Binary, SizeBytes = body.SizeBytes, Params = body.Params,
            };
        }

        var chunk = Slice(body.Text, body.MimeType, body.SourceEncoding, body.SizeBytes, offset, maxChars) with { Params = body.Params };
        if (chunk.NextOffset is not null && body.LooksLikeJson)
            warnings.Add($"{(side == BodySide.Request ? "Request" : "Response")} body is large JSON: use har_query_body " +
                         "(mode='structure', a path, fields or an aggregate) instead of paging through it.");
        return chunk;
    }

    private static BodyChunk Slice(string text, string? mime, string? encoding, long? size, int offset, int maxChars)
    {
        if (offset > text.Length)
            throw new HarScopeException(ErrorCodes.InvalidArgument, $"bodyOffset {offset} is beyond the body length ({text.Length} chars).");

        var end = Math.Min(text.Length, offset + maxChars);
        if (end < text.Length && end > offset && char.IsHighSurrogate(text[end - 1])) end--; // don't split surrogate pairs
        var hasMore = end < text.Length;

        return new BodyChunk
        {
            Available = true,
            MimeType = mime,
            SourceEncoding = encoding,
            SizeBytes = size,
            TotalChars = text.Length,
            Offset = offset,
            ReturnedChars = end - offset,
            Truncated = hasMore || offset > 0,
            NextOffset = hasMore ? end : null,
            Text = text[offset..end],
        };
    }

    private string Shorten(string value, ref bool truncated)
    {
        if (value.Length <= options.MaxValueChars) return value;
        truncated = true;
        return value[..options.MaxValueChars] + $"…[+{value.Length - options.MaxValueChars} chars]";
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }
}
