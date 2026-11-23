using HarScope.Core.Indexing;
using HarScope.Core.Security;

namespace HarScope.Core.Querying;

public sealed record CountItem(string Name, int Count);

public sealed record BodyAvailability(int RequestBodies, int ResponseBodies, int ResponsesWithoutBody, int Base64Responses);

public sealed record OverviewResult(
    string CaptureId,
    string File,
    string Revision,
    int TotalEntries,
    string? Creator,
    string? Browser,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    double? DurationMs,
    int FailedCount,
    int UniqueEndpoints,
    int HostCount,
    IReadOnlyDictionary<string, int> Methods,
    IReadOnlyDictionary<string, int> StatusClasses,
    IReadOnlyList<CountItem> StatusCodes,
    IReadOnlyList<CountItem> Hosts,
    IReadOnlyList<CountItem> ContentTypes,
    IReadOnlyList<CountItem>? ResourceTypes,
    IReadOnlyList<EntryRow> Slowest,
    BodyAvailability Bodies,
    IReadOnlyList<string>? Warnings);

/// <summary>Level 1: capture-wide statistics. Never touches bodies.</summary>
public sealed class OverviewService(Redactor redactor)
{
    private const int TopHosts = 15;
    private const int TopTypes = 10;
    private const int TopStatus = 15;

    public OverviewResult Build(CaptureHandle handle, int slowest = 5)
    {
        var c = handle.Capture;
        var e = c.Entries;
        slowest = Math.Clamp(slowest, 0, 20);

        var start = c.FirstStart;
        var end = c.LastEnd;
        var hosts = e.GroupBy(x => x.Host, StringComparer.OrdinalIgnoreCase).ToList();
        var resourceTypes = Top(e.Where(x => x.ResourceType is not null).Select(x => x.ResourceType!), TopTypes);

        var warnings = c.Warnings.Concat(handle.Warnings).ToList();
        if (hosts.Count > TopHosts) warnings.Add($"Only the top {TopHosts} of {hosts.Count} hosts are listed.");

        return new OverviewResult(
            c.CaptureId,
            c.RelativePath,
            c.Fingerprint.Revision,
            e.Count,
            c.Creator,
            c.Browser,
            start,
            end,
            start is not null && end is not null ? Math.Round((end.Value - start.Value).TotalMilliseconds) : null,
            e.Count(x => x.IsFailed),
            e.Select(x => $"{x.Method} {x.Host}{x.Path}").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            hosts.Count,
            e.GroupBy(x => x.Method == "" ? "(none)" : x.Method).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count()),
            e.GroupBy(x => StatusClass(x.Status)).OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count()),
            Top(e.Select(x => x.Status.ToString()), TopStatus),
            hosts.OrderByDescending(g => g.Count()).Take(TopHosts).Select(g => new CountItem(g.Key, g.Count())).ToList(),
            Top(e.Select(x => x.MimeType ?? "(none)"), TopTypes),
            resourceTypes.Count > 0 ? resourceTypes : null,
            e.Where(x => x.TimeMs is not null).OrderByDescending(x => x.TimeMs).Take(slowest)
                .Select(x => EntryRow.From(x, redactor)).ToList(),
            new BodyAvailability(
                e.Count(x => x.HasRequestBody),
                e.Count(x => x.HasResponseBody),
                e.Count(x => !x.HasResponseBody && x.Status is not (0 or 204 or 304) && !x.IsRedirect),
                e.Count(x => x.ResponseBase64)),
            warnings.Count > 0 ? warnings : null);
    }

    private static string StatusClass(int status) => status switch
    {
        0 => "noResponse",
        >= 100 and < 600 => $"{status / 100}xx",
        _ => "other",
    };

    private static List<CountItem> Top(IEnumerable<string> values, int n) =>
        values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(n).Select(g => new CountItem(g.Key, g.Count())).ToList();
}
