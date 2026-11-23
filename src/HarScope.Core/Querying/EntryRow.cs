using HarScope.Core.Models;
using HarScope.Core.Security;

namespace HarScope.Core.Querying;

/// <summary>Compact one-line view of an entry, used by search results and overview lists.</summary>
public sealed record EntryRow(
    string Id,
    DateTimeOffset? T,
    string Method,
    string Host,
    string Path,
    int Status,
    int? Ms,
    string? Mime,
    IReadOnlyList<string>? Flags)
{
    private const int MaxPathChars = 200;

    public static EntryRow From(HarEntry e, Redactor redactor)
    {
        var path = e.Query is null ? e.Path : redactor.Url($"{e.Path}?{e.Query}");
        if (path.Length > MaxPathChars) path = path[..MaxPathChars] + "…";

        return new EntryRow(e.Id, e.Started, e.Method, e.Host, path, e.Status,
            e.TimeMs is { } ms ? (int)Math.Round(ms) : null, e.MimeType, FlagsOf(e));
    }

    public static IReadOnlyList<string>? FlagsOf(HarEntry e)
    {
        List<string>? flags = null;
        void Add(string f) => (flags ??= []).Add(f);

        if (e.IsFailed) Add("failed");
        if (e.ErrorText is { } err) Add($"netError:{(err.Length > 60 ? err[..60] : err)}");
        if (e.IsRedirect) Add("redirect");
        if (e.HasRequestBody) Add("reqBody");
        if (e.HasResponseBody) Add(e.ResponseBase64 ? "resBody:base64" : "resBody");
        else if (e.Status is not (0 or 204 or 304) && !e.IsRedirect) Add("noResBody");
        if (e.Correlation.Count > 0) Add("correlated");
        return flags;
    }
}
