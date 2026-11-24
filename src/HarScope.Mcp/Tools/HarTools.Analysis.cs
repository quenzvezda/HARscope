using System.ComponentModel;
using HarScope.Core;
using HarScope.Core.Analysis;
using HarScope.Core.Indexing;
using HarScope.Core.Models;
using ModelContextProtocol.Server;

namespace HarScope.Mcp.Tools;

/// <summary>Analysis tools: flow, comparison and reproduction templates.</summary>
public sealed partial class HarTools
{
    [McpServerTool(Name = "har_trace_flow", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Chronological timeline of a capture grouped into bursts that usually match one user action (click, page " +
                 "load). Each group lists its calls (offset ms from group start, status, duration, flags incl. 'slow') and " +
                 "counts failures. Grouping by time gap is a HEURISTIC; 'confirmedLinks' lists data-backed relations " +
                 "(shared trace/correlation header values, redirect targets) and 'failureStreaks' consecutive failures. " +
                 "Static assets (images, fonts, css, js) and data: URLs are hidden by default." + UntrustedNote)]
    public string HarTraceFlow(
        [Description("Capture to trace. Defaults to the active capture.")] string? captureId = null,
        [Description("Only entries started at or after this ISO-8601 timestamp.")] string? from = null,
        [Description("Only entries started at or before this ISO-8601 timestamp.")] string? to = null,
        [Description("Only entries whose path contains this text.")] string? pathContains = null,
        [Description("Quiet gap in ms that starts a new group (default 800). Larger = fewer, bigger groups.")] int gapMs = 800,
        [Description("Calls at or above this duration are flagged 'slow' (default 1000 ms).")] int slowMs = 1_000,
        [Description("Include static assets (images, fonts, stylesheets, scripts).")] bool includeStatic = false,
        [Description("Groups to skip (use nextOffset).")] int offset = 0,
        [Description("Groups per page, default 10, maximum 50.")] int? limit = null) => Run(() =>
        flow.Trace(store.Get(captureId), new FlowQuery
        {
            From = ParseTime(from, nameof(from)), To = ParseTime(to, nameof(to)), PathContains = pathContains,
            GapMs = gapMs, SlowMs = slowMs, IncludeStatic = includeStatic, Offset = offset, Limit = limit,
        }));

    [McpServerTool(Name = "har_compare_entries", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Structured diff of two entries (same or different captures): status, query params, request/response " +
                 "headers (volatile ones like date or x-request-id ignored unless includeVolatileHeaders) and bodies. " +
                 "JSON bodies are diffed per path (added/removed/changed/typeChanged, arrays by index); other text bodies " +
                 "report the first differing position with context. Secret header/query values stay redacted; equal " +
                 "fingerprints mean equal values." + UntrustedNote)]
    public string HarCompareEntries(
        [Description("First entry ID (e.g. the successful call).")] string entryIdA,
        [Description("Second entry ID (e.g. the failed call).")] string entryIdB,
        [Description("JSON paths to ignore in body diffs, e.g. ['$.meta.timestamp', '$.data[*].updatedAt'].")] string[]? ignorePaths = null,
        [Description("Also diff volatile headers (date, etag, trace ids...).")] bool includeVolatileHeaders = false,
        [Description("Maximum body differences listed per body (default 50, max 200).")] int maxDiffs = 50) => Run(() =>
    {
        var (ha, a) = store.GetEntry(entryIdA);
        var (hb, b) = store.GetEntry(entryIdB);
        return compare.CompareEntries(ha, a, hb, b, ignorePaths, includeVolatileHeaders, maxDiffs);
    });

    [McpServerTool(Name = "har_compare_captures", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Compare the same API calls across two opened captures, e.g. before vs after a fix. Calls are paired by " +
                 "method + path + sorted query (host ignored) in order of occurrence. Returns per pair whether status, " +
                 "request body and response body are the same (JSON compared structurally), differing pairs first, plus " +
                 "calls present only in A or only in B. Static assets and data: URLs are skipped by default. Drill into a " +
                 "pair with har_compare_entries." + UntrustedNote)]
    public string HarCompareCaptures(
        [Description("captureId of the baseline capture (har_open both files first).")] string captureA,
        [Description("captureId of the capture to compare against A.")] string captureB,
        [Description("Only calls whose path contains this text.")] string? pathContains = null,
        [Description("Only calls with this HTTP method.")] string? method = null,
        [Description("JSON paths ignored when comparing bodies, e.g. ['$.timestamp'].")] string[]? ignorePaths = null,
        [Description("Return only pairs that differ.")] bool differentOnly = false,
        [Description("Include static assets.")] bool includeStatic = false,
        [Description("Pairs to skip (use nextOffset).")] int offset = 0,
        [Description("Pairs per page, default 30, maximum 100.")] int? limit = null) => Run(() =>
        compare.CompareCaptures(store.Get(captureA), store.Get(captureB), new CaptureCompareQuery
        {
            PathContains = pathContains, Method = method, IgnorePaths = ignorePaths, DifferentOnly = differentOnly,
            IncludeStatic = includeStatic, Offset = offset, Limit = limit,
        }));

    [McpServerTool(Name = "har_repro", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Build sanitized reproduction templates for up to 10 entries, in chronological order: REST Client / " +
                 "IntelliJ '.http' (default) or bash cURL. NOTHING IS SENT. Secret headers, cookies, query params and " +
                 "secret-looking JSON/form body fields become {{placeholders}} listed in 'variables'; volatile and " +
                 "browser-managed headers are dropped unless allHeaders. 'dependencies' lists request values that first " +
                 "appeared in an earlier response (e.g. a token from login, an ID from a create call). Recorded requests " +
                 "may not be reproducible (expired sessions, CSRF, changed server state)." + UntrustedNote)]
    public string HarRepro(
        [Description("Entry IDs to include (1-10).")] string[] entryIds,
        [Description("'http' (default) or 'curl'.")] string format = "http",
        [Description("Keep all non-secret headers instead of a minimal set.")] bool allHeaders = false) => Run(() =>
    {
        var items = (entryIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct()
            .Select(id => store.GetEntry(id)).ToList();
        var fmt = format?.Trim().ToLowerInvariant() switch
        {
            null or "" or "http" => ReproFormat.Http,
            "curl" => ReproFormat.Curl,
            _ => throw new HarScopeException(ErrorCodes.InvalidArgument, "format must be 'http' or 'curl'."),
        };
        return repro.Build(items.Select(i => (i.Handle, i.Entry)).ToList(), fmt, allHeaders);
    });
}
