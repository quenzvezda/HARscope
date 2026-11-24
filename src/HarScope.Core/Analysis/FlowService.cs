using HarScope.Core.Indexing;
using HarScope.Core.Models;
using HarScope.Core.Querying;
using HarScope.Core.Security;

namespace HarScope.Core.Analysis;

public sealed record FlowQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? PathContains { get; init; }
    /// <summary>A new group starts when a request begins this long after everything before it finished.</summary>
    public int GapMs { get; init; } = 800;
    public int SlowMs { get; init; } = 1_000;
    public bool IncludeStatic { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }
}

public sealed record FlowCall(string Id, int OffsetMs, string Method, string Host, string Path, int Status, int? Ms,
    IReadOnlyList<string>? Flags);

public sealed record FlowGroup(
    int Group,
    DateTimeOffset Start,
    DateTimeOffset End,
    int DurationMs,
    int? GapBeforeMs,
    int Calls,
    int Failed,
    int Slow,
    string? FirstFailure,
    IReadOnlyList<FlowCall> CallList,
    int MoreCalls);

/// <summary>A relationship backed by data in the HAR (shared trace header, redirect target), not by timing.</summary>
public sealed record FlowLink(string Kind, string Detail, IReadOnlyList<string> Entries);

public sealed record FailureStreak(string From, string To, int Count);

public sealed record FlowResult
{
    public required string CaptureId { get; init; }
    public required int GapMs { get; init; }
    public required int ConsideredEntries { get; init; }
    public required int HiddenStatic { get; init; }
    public required int TotalGroups { get; init; }
    public required int Offset { get; init; }
    public required int ReturnedCount { get; init; }
    public required bool HasMore { get; init; }
    public int? NextOffset { get; init; }
    public required IReadOnlyList<FlowGroup> Groups { get; init; }
    public IReadOnlyList<FlowLink>? ConfirmedLinks { get; init; }
    public IReadOnlyList<FailureStreak>? FailureStreaks { get; init; }
    public bool Truncated { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>Chronological view of a capture, grouped into bursts that usually correspond to one user action.</summary>
public sealed class FlowService(HarScopeOptions options, Redactor redactor)
{
    private const int DefaultGroups = 10;
    private const int MaxGroups = 50;
    private const int MaxCallsPerGroup = 25;
    private const int MaxLinks = 30;

    public FlowResult Trace(CaptureHandle handle, FlowQuery q)
    {
        if (q.Offset < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "offset must be >= 0.");
        if (q.Limit is < 1) throw new HarScopeException(ErrorCodes.InvalidArgument, "limit must be >= 1.");
        if (q.GapMs < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "gapMs must be >= 0.");

        var all = handle.Capture.Entries
            .Where(e => e.Started is not null && !e.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .Where(e => q.From is not { } f || e.Started >= f)
            .Where(e => q.To is not { } t || e.Started <= t)
            .Where(e => q.PathContains is not { Length: > 0 } p || e.Path.Contains(p, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Started).ThenBy(e => e.Number)
            .ToList();
        var entries = q.IncludeStatic ? all : all.Where(e => !CompareService.IsStatic(e)).ToList();

        var groups = BuildGroups(entries, q);
        var limit = Math.Min(q.Limit ?? DefaultGroups, MaxGroups);
        var page = groups.Skip(q.Offset).Take(limit).ToList();

        var notes = new List<string>(handle.Warnings)
        {
            $"Groups are a heuristic: a new group starts after a quiet gap of {q.GapMs} ms. Requests in one group are " +
            "close in time, which does not prove they belong to the same user action. confirmedLinks are backed by data.",
        };
        if (handle.Capture.Entries.Any(e => e.Started is null)) notes.Add("Entries without startedDateTime are not shown.");

        var result = new FlowResult
        {
            CaptureId = handle.Capture.CaptureId,
            GapMs = q.GapMs,
            ConsideredEntries = entries.Count,
            HiddenStatic = all.Count - entries.Count,
            TotalGroups = groups.Count,
            Offset = q.Offset,
            ReturnedCount = page.Count,
            HasMore = q.Offset + page.Count < groups.Count,
            NextOffset = q.Offset + page.Count < groups.Count ? q.Offset + page.Count : null,
            Groups = page,
            ConfirmedLinks = Links(handle.Capture, entries) is { Count: > 0 } links ? links : null,
            FailureStreaks = Streaks(entries) is { Count: > 0 } s ? s : null,
            Notes = notes,
        };

        while (page.Count > 1 && HarJson.Serialize(result).Length > options.MaxOutputChars)
        {
            page.RemoveAt(page.Count - 1);
            result = result with
            {
                Groups = page, ReturnedCount = page.Count, HasMore = true, NextOffset = q.Offset + page.Count, Truncated = true,
            };
        }
        return result;
    }

    private List<FlowGroup> BuildGroups(List<HarEntry> entries, FlowQuery q)
    {
        var groups = new List<FlowGroup>();
        var current = new List<HarEntry>();
        DateTimeOffset? groupEnd = null;
        DateTimeOffset? previousEnd = null;

        void Flush()
        {
            if (current.Count == 0) return;
            var start = current[0].Started!.Value;
            var end = groupEnd!.Value;
            var gapBefore = previousEnd is { } pe ? (int?)Math.Max(0, (int)(start - pe).TotalMilliseconds) : null;
            var calls = current.Take(MaxCallsPerGroup).Select(e => Call(e, start, q.SlowMs)).ToList();
            groups.Add(new FlowGroup(
                groups.Count + 1, start, end, (int)Math.Round((end - start).TotalMilliseconds), gapBefore, current.Count,
                current.Count(e => e.IsFailed), current.Count(e => e.TimeMs >= q.SlowMs),
                current.FirstOrDefault(e => e.IsFailed)?.Id, calls, Math.Max(0, current.Count - MaxCallsPerGroup)));
            previousEnd = end;
            current = [];
            groupEnd = null;
        }

        foreach (var e in entries)
        {
            var start = e.Started!.Value;
            var end = start.AddMilliseconds(e.TimeMs ?? 0);
            if (groupEnd is { } ge && (start - ge).TotalMilliseconds > q.GapMs) Flush();
            current.Add(e);
            groupEnd = groupEnd is { } g && g > end ? g : end;
        }
        Flush();
        return groups;
    }

    private FlowCall Call(HarEntry e, DateTimeOffset groupStart, int slowMs)
    {
        var row = EntryRow.From(e, redactor);
        var flags = row.Flags?.ToList() ?? [];
        if (e.TimeMs >= slowMs) flags.Add("slow");
        return new FlowCall(e.Id, (int)Math.Round((e.Started!.Value - groupStart).TotalMilliseconds), e.Method, e.Host,
            row.Path, e.Status, row.Ms, flags.Count > 0 ? flags : null);
    }

    private List<FlowLink> Links(HarCapture capture, List<HarEntry> entries)
    {
        var links = new List<FlowLink>();

        // Shared trace/correlation header values.
        foreach (var g in entries
                     .SelectMany(e => e.Correlation.Select(kv => (Header: kv.Key, Value: kv.Value, e.Id)))
                     .GroupBy(x => (x.Header, x.Value))
                     .Where(g => g.Select(x => x.Id).Distinct().Count() > 1))
        {
            links.Add(new FlowLink("correlation", $"{g.Key.Header}={Short(g.Key.Value)}",
                g.Select(x => x.Id).Distinct().Take(20).ToList()));
        }

        // Redirects: the next request to the redirect target.
        foreach (var e in entries.Where(e => e.RedirectUrl is not null))
        {
            var target = Resolve(e.Url, e.RedirectUrl!);
            var next = capture.Entries.FirstOrDefault(n =>
                n.Started >= e.Started && n.Number != e.Number && string.Equals(n.Url, target, StringComparison.Ordinal));
            links.Add(new FlowLink("redirect", $"{e.Status} -> {redactor.Url(target)}",
                next is null ? [e.Id] : [e.Id, next.Id]));
        }

        return links.Take(MaxLinks).ToList();
    }

    private static List<FailureStreak> Streaks(List<HarEntry> entries)
    {
        var result = new List<FailureStreak>();
        var run = new List<HarEntry>();
        foreach (var e in entries.Append(null))
        {
            if (e is not null && e.IsFailed) { run.Add(e); continue; }
            if (run.Count >= 2) result.Add(new FailureStreak(run[0].Id, run[^1].Id, run.Count));
            run.Clear();
        }
        return result.Take(20).ToList();
    }

    private static string Resolve(string baseUrl, string target) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, target, out var r) ? r.AbsoluteUri : target;

    private static string Short(string v) => v.Length <= 80 ? v : v[..80] + "…";
}
