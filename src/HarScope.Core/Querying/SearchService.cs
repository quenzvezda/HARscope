using HarScope.Core.Indexing;
using HarScope.Core.Models;
using HarScope.Core.Parsing;
using HarScope.Core.Security;

namespace HarScope.Core.Querying;

public enum SearchSort { Time, Duration, Status }

/// <summary>Level 2 filter. All criteria are optional and combined with AND.</summary>
public sealed record SearchQuery
{
    public string? UrlContains { get; init; }
    public string? Method { get; init; }
    public int? Status { get; init; }
    public int? StatusMin { get; init; }
    public int? StatusMax { get; init; }
    public string? Host { get; init; }
    public string? PathContains { get; init; }
    public string? MimeType { get; init; }
    public string? ResourceType { get; init; }
    public double? MinDurationMs { get; init; }
    public double? MaxDurationMs { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? RequestHeader { get; init; }
    public string? ResponseHeader { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>Case-insensitive substring of the decoded text body (binary bodies are skipped).</summary>
    public string? BodyContains { get; init; }
    /// <summary>Which body BodyContains looks at; null means request or response.</summary>
    public BodySide? BodySide { get; init; }
    public bool FailedOnly { get; init; }
    /// <summary>Inline data: URLs (embedded images/fonts) are hidden unless this is true.</summary>
    public bool IncludeDataUrls { get; init; }
    public SearchSort Sort { get; init; } = SearchSort.Time;
    public bool Descending { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }
}

public sealed record SearchResult(
    string CaptureId,
    int TotalMatches,
    int Offset,
    int ReturnedCount,
    bool HasMore,
    int? NextOffset,
    bool Truncated,
    IReadOnlyList<EntryRow> Entries,
    IReadOnlyList<string>? Warnings);

public sealed class SearchService(HarScopeOptions options, Redactor redactor)
{
    public SearchResult Search(CaptureHandle handle, SearchQuery query)
    {
        Validate(query);
        var warnings = new List<string>(handle.Warnings);

        var limit = query.Limit ?? options.DefaultSearchLimit;
        if (limit > options.MaxSearchLimit)
        {
            warnings.Add($"limit {limit} exceeds the maximum; using {options.MaxSearchLimit}.");
            limit = options.MaxSearchLimit;
        }

        var hiddenDataUrls = 0;
        var matches = handle.Capture.Entries.Where(e =>
        {
            if (!Matches(e, query)) return false;
            if (query.IncludeDataUrls || !e.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            hiddenDataUrls++;
            return false;
        });
        var ordered = Order(matches, query).ToList();
        if (hiddenDataUrls > 0)
            warnings.Add($"{hiddenDataUrls} inline data: URL entries hidden (includeDataUrls=true to show them).");
        var offset = Math.Max(0, query.Offset);

        var page = ordered.Skip(offset).Take(limit).Select(e => EntryRow.From(e, redactor)).ToList();

        // Enforce the output budget by dropping rows from the end; the caller pages on with nextOffset.
        var truncated = false;
        SearchResult Build() => new(
            handle.Capture.CaptureId, ordered.Count, offset, page.Count,
            offset + page.Count < ordered.Count,
            offset + page.Count < ordered.Count ? offset + page.Count : null,
            truncated, page, warnings.Count > 0 ? warnings : null);

        var result = Build();
        while (page.Count > 1 && HarJson.Serialize(result).Length > options.MaxOutputChars)
        {
            page.RemoveRange(page.Count - Math.Max(1, page.Count / 4), Math.Max(1, page.Count / 4));
            if (!truncated)
            {
                truncated = true;
                warnings.Add("Page shortened to stay within the output limit; continue with nextOffset.");
            }
            result = Build();
        }

        return result;
    }

    private static void Validate(SearchQuery q)
    {
        if (q.Offset < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "offset must be >= 0.");
        if (q.Limit is < 1) throw new HarScopeException(ErrorCodes.InvalidArgument, "limit must be >= 1.");
        if (q.StatusMin is { } min && q.StatusMax is { } max && min > max)
            throw new HarScopeException(ErrorCodes.InvalidArgument, "statusMin must be <= statusMax.");
        if (q.From is { } from && q.To is { } to && from > to)
            throw new HarScopeException(ErrorCodes.InvalidArgument, "from must be <= to.");
    }

    private static bool Matches(HarEntry e, SearchQuery q)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        if (q.FailedOnly && !e.IsFailed) return false;
        if (q.Method is { Length: > 0 } m && !string.Equals(e.Method, m.Trim(), ci)) return false;
        if (q.Status is { } s && e.Status != s) return false;
        if (q.StatusMin is { } smin && e.Status < smin) return false;
        if (q.StatusMax is { } smax && e.Status > smax) return false;
        if (q.UrlContains is { Length: > 0 } u && !e.Url.Contains(u, ci)) return false;
        if (q.Host is { Length: > 0 } h && !e.Host.Contains(h, ci)) return false;
        if (q.PathContains is { Length: > 0 } p && !e.Path.Contains(p, ci)) return false;
        if (q.MimeType is { Length: > 0 } mt && !(e.MimeType?.Contains(mt, ci) ?? false)) return false;
        if (q.ResourceType is { Length: > 0 } rt && !string.Equals(e.ResourceType, rt, ci)) return false;
        if (q.MinDurationMs is { } dmin && !(e.TimeMs >= dmin)) return false;
        if (q.MaxDurationMs is { } dmax && !(e.TimeMs <= dmax)) return false;
        if (q.From is { } from && !(e.Started >= from)) return false;
        if (q.To is { } to && !(e.Started <= to)) return false;
        if (q.CorrelationId is { Length: > 0 } cid && !e.Correlation.Values.Any(v => v.Contains(cid, ci))) return false;
        if (q.RequestHeader is { Length: > 0 } rqh && !HasHeader(e, "request", rqh)) return false;
        if (q.ResponseHeader is { Length: > 0 } rsh && !HasHeader(e, "response", rsh)) return false;
        if (q.BodyContains is { Length: > 0 } text && !BodyMatches(e, text, q.BodySide)) return false;
        return true;
    }

    private static bool HasHeader(HarEntry e, string side, string name) =>
        Json.Array(Json.Object(e.Raw, side), "headers")
            .Any(h => string.Equals(Json.String(h, "name"), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool BodyMatches(HarEntry e, string text, BodySide? side)
    {
        bool In(BodySide s)
        {
            var body = BodyReader.Read(e, s, int.MaxValue);
            if (body.Text is not null) return body.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
            // Form posts without postData.text: match parameter names/values.
            return body.Params?.Any(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                         (p.Value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)) ?? false;
        }

        return side switch
        {
            Querying.BodySide.Request => e.HasRequestBody && In(Querying.BodySide.Request),
            Querying.BodySide.Response => e.HasResponseBody && In(Querying.BodySide.Response),
            _ => (e.HasRequestBody && In(Querying.BodySide.Request)) || (e.HasResponseBody && In(Querying.BodySide.Response)),
        };
    }

    private static IEnumerable<HarEntry> Order(IEnumerable<HarEntry> entries, SearchQuery q)
    {
        // Entries without a start time always sort last; ties fall back to original position.
        IOrderedEnumerable<HarEntry> ordered = q.Sort switch
        {
            SearchSort.Duration => q.Descending
                ? entries.OrderByDescending(e => e.TimeMs ?? double.MinValue)
                : entries.OrderBy(e => e.TimeMs ?? double.MaxValue),
            SearchSort.Status => q.Descending ? entries.OrderByDescending(e => e.Status) : entries.OrderBy(e => e.Status),
            _ => q.Descending
                ? entries.OrderBy(e => e.Started is null).ThenByDescending(e => e.Started)
                : entries.OrderBy(e => e.Started is null).ThenBy(e => e.Started),
        };
        return q.Descending && q.Sort == SearchSort.Time
            ? ordered.ThenByDescending(e => e.Number)
            : ordered.ThenBy(e => e.Number);
    }
}
