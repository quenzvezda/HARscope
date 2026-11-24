using System.Text.Json;
using HarScope.Core.Indexing;
using HarScope.Core.Models;
using HarScope.Core.Parsing;
using HarScope.Core.Querying;
using HarScope.Core.Security;

namespace HarScope.Core.Analysis;

public sealed record EntryRef(string Id, string Method, string Url, int Status, int? Ms);

public sealed record NameDiff(string Name, string Change, string? A = null, string? B = null);

public sealed record BodyDiff
{
    public required string ComparedAs { get; init; } // json | text | none
    public required bool Equal { get; init; }
    public int? AChars { get; init; }
    public int? BChars { get; init; }
    public int? TotalDiffs { get; init; }
    public IReadOnlyList<DiffItem>? Diffs { get; init; }
    public bool Truncated { get; init; }
    public int? FirstDifferenceAt { get; init; }
    public string? AContext { get; init; }
    public string? BContext { get; init; }
    public string? Note { get; init; }
}

public sealed record MessageDiff(
    IReadOnlyList<NameDiff>? Headers,
    IReadOnlyList<NameDiff>? QueryParams,
    BodyDiff Body);

public sealed record EntryComparison
{
    public required EntryRef A { get; init; }
    public required EntryRef B { get; init; }
    public required bool Identical { get; init; }
    public IReadOnlyList<string>? Differences { get; init; }
    public required MessageDiff Request { get; init; }
    public required MessageDiff Response { get; init; }
    public double? DeltaMs { get; init; }
    public int IgnoredHeaderChanges { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record PairRow(
    string Key,
    string A,
    string B,
    int StatusA,
    int StatusB,
    int? MsA,
    int? MsB,
    string Request,
    string Response,
    int? ResponseDiffs);

public sealed record UnpairedRow(string Key, IReadOnlyList<string> Ids);

public sealed record CaptureComparison
{
    public required string CaptureA { get; init; }
    public required string CaptureB { get; init; }
    public required int ComparedA { get; init; }
    public required int ComparedB { get; init; }
    public required int Pairs { get; init; }
    public required int IdenticalPairs { get; init; }
    public required int DifferentPairs { get; init; }
    public required int StatusChanged { get; init; }
    public required int Offset { get; init; }
    public required int ReturnedCount { get; init; }
    public required bool HasMore { get; init; }
    public int? NextOffset { get; init; }
    public required IReadOnlyList<PairRow> PairRows { get; init; }
    public required int OnlyInACount { get; init; }
    public required IReadOnlyList<UnpairedRow> OnlyInA { get; init; }
    public required int OnlyInBCount { get; init; }
    public required IReadOnlyList<UnpairedRow> OnlyInB { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record CaptureCompareQuery
{
    public string? PathContains { get; init; }
    public string? Method { get; init; }
    public bool IncludeStatic { get; init; }
    public bool DifferentOnly { get; init; }
    public IReadOnlyList<string>? IgnorePaths { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }
}

/// <summary>Diffs of two entries (same or different captures) and pairing of calls across two captures.</summary>
public sealed class CompareService(HarScopeOptions options, Redactor redactor, JsonBodyCache cache)
{
    private const int MaxUnpairedRows = 30;
    private const int DefaultPairLimit = 30;
    private const int MaxPairLimit = 100;
    private const int ContextChars = 80;

    /// <summary>Headers that change on every request and would only add noise.</summary>
    public static readonly HashSet<string> VolatileHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "age", "expires", "last-modified", "etag", "content-length", "traceparent", "tracestate",
        "x-request-id", "x-correlation-id", "correlation-id", "request-id", "x-amzn-trace-id", "x-amzn-requestid",
        "x-b3-traceid", "x-b3-spanid", "cf-ray", "server-timing", "report-to", "nel", "x-ms-request-id",
        "x-cloud-trace-context", "x-envoy-upstream-service-time", "x-runtime",
    };

    public EntryComparison CompareEntries(CaptureHandle ha, HarEntry a, CaptureHandle hb, HarEntry b,
        IReadOnlyList<string>? ignorePaths = null, bool includeVolatileHeaders = false, int maxDiffs = 50)
    {
        maxDiffs = Math.Clamp(maxDiffs, 1, 200);
        var ignoredHeaders = 0;

        var reqA = Json.Object(a.Raw, "request");
        var reqB = Json.Object(b.Raw, "request");
        var resA = Json.Object(a.Raw, "response");
        var resB = Json.Object(b.Raw, "response");

        var request = new MessageDiff(
            NullIfEmpty(HeaderDiff(reqA, reqB, includeVolatileHeaders, ref ignoredHeaders)),
            NullIfEmpty(QueryDiff(a, b)),
            Body(ha.Capture, a, hb.Capture, b, BodySide.Request, ignorePaths, maxDiffs));
        var response = new MessageDiff(
            NullIfEmpty(HeaderDiff(resA, resB, includeVolatileHeaders, ref ignoredHeaders)),
            null,
            Body(ha.Capture, a, hb.Capture, b, BodySide.Response, ignorePaths, maxDiffs));

        var differences = new List<string>();
        if (a.Method != b.Method) differences.Add($"method {a.Method} -> {b.Method}");
        if (a.Path != b.Path) differences.Add("path differs");
        if (a.Host != b.Host) differences.Add($"host {a.Host} -> {b.Host}");
        if (a.Status != b.Status) differences.Add($"status {a.Status} -> {b.Status}");
        if (request.QueryParams is not null) differences.Add($"{request.QueryParams.Count} query param difference(s)");
        if (request.Headers is not null) differences.Add($"{request.Headers.Count} request header difference(s)");
        if (!request.Body.Equal) differences.Add("request body differs");
        if (response.Headers is not null) differences.Add($"{response.Headers.Count} response header difference(s)");
        if (!response.Body.Equal) differences.Add("response body differs");

        var identical = a.Method == b.Method && a.Path == b.Path && a.Status == b.Status &&
                        request.QueryParams is null && request.Body.Equal && response.Body.Equal;

        var warnings = ha.Warnings.Concat(hb.Warnings).Distinct().ToList();
        if (a.Path != b.Path) warnings.Add("The entries are different endpoints; the diff may not be meaningful.");

        return new EntryComparison
        {
            A = Ref(a),
            B = Ref(b),
            Identical = identical,
            Differences = differences.Count > 0 ? differences : null,
            Request = request,
            Response = response,
            DeltaMs = a.TimeMs is { } ta && b.TimeMs is { } tb ? Math.Round(tb - ta, 1) : null,
            IgnoredHeaderChanges = ignoredHeaders,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    public CaptureComparison CompareCaptures(CaptureHandle ha, CaptureHandle hb, CaptureCompareQuery q)
    {
        if (q.Offset < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "offset must be >= 0.");
        if (q.Limit is < 1) throw new HarScopeException(ErrorCodes.InvalidArgument, "limit must be >= 1.");
        var limit = Math.Min(q.Limit ?? DefaultPairLimit, MaxPairLimit);

        var listA = Candidates(ha.Capture, q);
        var listB = Candidates(hb.Capture, q);
        var groupsA = listA.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var groupsB = listB.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<(PairRow Row, int Rank)>();
        var onlyA = new List<UnpairedRow>();
        var onlyB = new List<UnpairedRow>();

        foreach (var (key, entriesA) in groupsA)
        {
            groupsB.TryGetValue(key, out var entriesB);
            entriesB ??= [];
            var n = Math.Min(entriesA.Count, entriesB.Count);
            for (var i = 0; i < n; i++) rows.Add(Pair(key, ha.Capture, entriesA[i], hb.Capture, entriesB[i], q.IgnorePaths));
            if (entriesA.Count > n) onlyA.Add(new UnpairedRow(key, entriesA.Skip(n).Select(e => e.Id).Take(10).ToList()));
            if (entriesB.Count > n) onlyB.Add(new UnpairedRow(key, entriesB.Skip(n).Select(e => e.Id).Take(10).ToList()));
        }
        foreach (var (key, entriesB) in groupsB)
            if (!groupsA.ContainsKey(key)) onlyB.Add(new UnpairedRow(key, entriesB.Select(e => e.Id).Take(10).ToList()));

        var different = rows.Count(r => r.Rank > 0);
        var statusChanged = rows.Count(r => r.Row.StatusA != r.Row.StatusB);
        var ordered = rows
            .Where(r => !q.DifferentOnly || r.Rank > 0)
            .OrderByDescending(r => r.Rank)
            .ThenBy(r => r.Row.Key, StringComparer.Ordinal)
            .Select(r => r.Row)
            .ToList();
        var page = ordered.Skip(q.Offset).Take(limit).ToList();

        var warnings = ha.Warnings.Concat(hb.Warnings).Distinct().ToList();
        warnings.Add("Calls are paired by method + path + sorted query string (host ignored), in order of occurrence. " +
                     "Use har_compare_entries on a pair for details.");
        if (rows.Count == 0) warnings.Add("No calls could be paired; check the filters or includeStatic.");

        var result = new CaptureComparison
        {
            CaptureA = ha.Capture.CaptureId,
            CaptureB = hb.Capture.CaptureId,
            ComparedA = listA.Count,
            ComparedB = listB.Count,
            Pairs = rows.Count,
            IdenticalPairs = rows.Count - different,
            DifferentPairs = different,
            StatusChanged = statusChanged,
            Offset = q.Offset,
            ReturnedCount = page.Count,
            HasMore = q.Offset + page.Count < ordered.Count,
            NextOffset = q.Offset + page.Count < ordered.Count ? q.Offset + page.Count : null,
            PairRows = page,
            OnlyInACount = onlyA.Sum(o => o.Ids.Count),
            OnlyInA = onlyA.OrderBy(o => o.Key, StringComparer.Ordinal).Take(MaxUnpairedRows).ToList(),
            OnlyInBCount = onlyB.Sum(o => o.Ids.Count),
            OnlyInB = onlyB.OrderBy(o => o.Key, StringComparer.Ordinal).Take(MaxUnpairedRows).ToList(),
            Truncated = onlyA.Count > MaxUnpairedRows || onlyB.Count > MaxUnpairedRows,
            Warnings = warnings,
        };

        // Keep within the output budget by shortening the page.
        while (result.PairRows.Count > 1 && HarJson.Serialize(result).Length > options.MaxOutputChars)
        {
            page.RemoveRange(page.Count - Math.Max(1, page.Count / 4), Math.Max(1, page.Count / 4));
            result = result with
            {
                PairRows = page, ReturnedCount = page.Count, HasMore = true, NextOffset = q.Offset + page.Count, Truncated = true,
            };
        }
        return result;
    }

    // ---------- helpers ----------

    private (PairRow Row, int Rank) Pair(string key, HarCapture ca, HarEntry a, HarCapture cb, HarEntry b, IReadOnlyList<string>? ignore)
    {
        var req = Body(ca, a, cb, b, BodySide.Request, ignore, 1);
        var res = Body(ca, a, cb, b, BodySide.Response, ignore, 1);

        static string Label(BodyDiff d) => d.ComparedAs == "none" ? "n/a" : d.Equal ? "same" : "different";

        // Rank: status change first, then response differences, then request differences.
        var rank = (a.Status != b.Status ? 4 : 0) + (!res.Equal ? 2 : 0) + (!req.Equal ? 1 : 0);
        return (new PairRow(key, a.Id, b.Id, a.Status, b.Status, Ms(a), Ms(b), Label(req), Label(res),
            res.ComparedAs == "json" && !res.Equal ? res.TotalDiffs : null), rank);
    }

    private static List<HarEntry> Candidates(HarCapture capture, CaptureCompareQuery q) => capture.Entries
        .Where(e => !e.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        .Where(e => q.IncludeStatic || !IsStatic(e))
        .Where(e => q.PathContains is not { Length: > 0 } p || e.Path.Contains(p, StringComparison.OrdinalIgnoreCase))
        .Where(e => q.Method is not { Length: > 0 } m || string.Equals(e.Method, m.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.Started is null).ThenBy(e => e.Started).ThenBy(e => e.Number)
        .ToList();

    public static bool IsStatic(HarEntry e)
    {
        if (e.ResourceType is "image" or "font" or "stylesheet" or "script" or "media" or "manifest" or "texttrack")
            return true;
        var mime = e.MimeType ?? "";
        return mime.StartsWith("image/") || mime.StartsWith("font/") || mime.StartsWith("audio/") || mime.StartsWith("video/") ||
               mime is "text/css" or "application/javascript" or "text/javascript" or "application/font-woff" or "application/wasm";
    }

    public static string Key(HarEntry e)
    {
        if (e.Query is null) return $"{e.Method} {e.Path}";
        var sorted = e.Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        return $"{e.Method} {e.Path}?{string.Join('&', sorted)}";
    }

    private BodyDiff Body(HarCapture ca, HarEntry a, HarCapture cb, HarEntry b, BodySide side,
        IReadOnlyList<string>? ignorePaths, int maxDiffs)
    {
        var pa = cache.Get(ca, a, side, options.MaxValueChars);
        var pb = cache.Get(cb, b, side, options.MaxValueChars);

        if (pa.Root is { } ra && pb.Root is { } rb)
        {
            var diff = JsonDiff.Compare(ra, rb, ignorePaths, maxDiffs);
            return new BodyDiff
            {
                ComparedAs = "json", Equal = diff.Total == 0,
                AChars = pa.Body.Text?.Length, BChars = pb.Body.Text?.Length,
                TotalDiffs = diff.Total, Diffs = diff.Items.Count > 0 ? diff.Items : null, Truncated = diff.Truncated,
            };
        }

        var ta = pa.Body.Text;
        var tb = pb.Body.Text;
        if (ta is null && tb is null)
        {
            var bothBinary = pa.Body.Binary && pb.Body.Binary;
            return new BodyDiff
            {
                ComparedAs = "none",
                Equal = bothBinary ? pa.Body.SizeBytes == pb.Body.SizeBytes : pa.Body.Available == pb.Body.Available,
                Note = bothBinary ? "Binary bodies: only sizes compared." : "No text body on either side.",
            };
        }

        if (ta is null || tb is null)
            return new BodyDiff
            {
                ComparedAs = "text", Equal = false, AChars = ta?.Length, BChars = tb?.Length,
                Note = ta is null ? $"A has no text body: {pa.Body.Reason}" : $"B has no text body: {pb.Body.Reason}",
            };

        if (ta == tb) return new BodyDiff { ComparedAs = "text", Equal = true, AChars = ta.Length, BChars = tb.Length };

        var first = 0;
        var max = Math.Min(ta.Length, tb.Length);
        while (first < max && ta[first] == tb[first]) first++;
        var start = Math.Max(0, first - ContextChars / 2);
        return new BodyDiff
        {
            ComparedAs = "text", Equal = false, AChars = ta.Length, BChars = tb.Length, FirstDifferenceAt = first,
            AContext = Snip(ta, start), BContext = Snip(tb, start),
        };
    }

    private static string Snip(string s, int start) =>
        start >= s.Length ? "" : s.Substring(start, Math.Min(ContextChars, s.Length - start));

    private List<NameDiff> HeaderDiff(JsonElement? a, JsonElement? b, bool includeVolatile, ref int ignored)
    {
        static Dictionary<string, string> Map(JsonElement? m) => Json.Array(m, "headers")
            .Select(h => (Name: Json.String(h, "name") ?? "", Value: Json.String(h, "value") ?? ""))
            .Where(h => h.Name.Length > 0 && !h.Name.StartsWith(':'))
            .GroupBy(h => h.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(h => h.Value)));

        var ma = Map(a);
        var mb = Map(b);
        var result = new List<NameDiff>();
        foreach (var name in ma.Keys.Union(mb.Keys).Order(StringComparer.Ordinal))
        {
            ma.TryGetValue(name, out var va);
            mb.TryGetValue(name, out var vb);
            if (va == vb) continue;
            if (!includeVolatile && VolatileHeaders.Contains(name)) { ignored++; continue; }

            var change = va is null ? "added" : vb is null ? "removed" : "changed";
            result.Add(new NameDiff(name, change,
                va is null ? null : Shorten(redactor.HeaderValue(name, va)),
                vb is null ? null : Shorten(redactor.HeaderValue(name, vb))));
        }
        return result;
    }

    private List<NameDiff> QueryDiff(HarEntry a, HarEntry b)
    {
        static Dictionary<string, string> Map(HarEntry e) => (e.Query ?? "")
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Unescape(p[0]))
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(p => p.Length > 1 ? Unescape(p[1]) : "")));

        var ma = Map(a);
        var mb = Map(b);
        var result = new List<NameDiff>();
        foreach (var name in ma.Keys.Union(mb.Keys).Order(StringComparer.Ordinal))
        {
            ma.TryGetValue(name, out var va);
            mb.TryGetValue(name, out var vb);
            if (va == vb) continue;
            result.Add(new NameDiff(name, va is null ? "added" : vb is null ? "removed" : "changed",
                va is null ? null : Shorten(redactor.QueryValue(name, va)),
                vb is null ? null : Shorten(redactor.QueryValue(name, vb))));
        }
        return result;
    }

    private EntryRef Ref(HarEntry e) => new(e.Id, e.Method, redactor.Url(e.Url), e.Status, Ms(e));

    private static int? Ms(HarEntry e) => e.TimeMs is { } t ? (int)Math.Round(t) : null;

    private string Shorten(string v) => v.Length <= options.MaxValueChars ? v : v[..options.MaxValueChars] + "…";

    private static IReadOnlyList<T>? NullIfEmpty<T>(List<T> list) => list.Count > 0 ? list : null;

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }
}
