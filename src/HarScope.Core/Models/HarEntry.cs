using System.Text.Json;

namespace HarScope.Core.Models;

/// <summary>Index row for one entry of <c>log.entries</c>. Small by design: no bodies, no full header lists.</summary>
public sealed class HarEntry
{
    /// <summary>Stable ID: "&lt;captureId&gt;:entry-&lt;number&gt;".</summary>
    public required string Id { get; init; }

    /// <summary>1-based position in the original <c>log.entries</c> array.</summary>
    public required int Number { get; init; }

    public DateTimeOffset? Started { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string Host { get; init; }
    public required string Path { get; init; }
    public string? Query { get; init; }

    /// <summary>0 when the browser recorded no response (blocked, aborted, network error).</summary>
    public int Status { get; init; }
    public string? StatusText { get; init; }

    /// <summary>Total time in ms; null when missing or negative in the HAR.</summary>
    public double? TimeMs { get; init; }

    public string? MimeType { get; init; }
    public string? ResourceType { get; init; }
    public string? ErrorText { get; init; }
    public string? RedirectUrl { get; init; }

    public bool HasRequestBody { get; init; }
    public long? RequestBodySize { get; init; }
    public bool HasResponseBody { get; init; }
    public long? ResponseBodySize { get; init; }
    public bool ResponseBase64 { get; init; }

    /// <summary>Selected trace/correlation header values (request and response), lower-cased names.</summary>
    public required IReadOnlyDictionary<string, string> Correlation { get; init; }

    public bool IsFailed => Status == 0 || Status >= 400 || ErrorText is not null;
    public bool IsRedirect => Status is >= 300 and < 400;

    /// <summary>The original JSON entry. Read on demand for headers and bodies.</summary>
    public required JsonElement Raw { get; init; }
}
