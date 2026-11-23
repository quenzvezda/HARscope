using System.Text.Json;

namespace HarScope.Core.Models;

/// <summary>
/// A loaded HAR file: lightweight metadata index plus the parsed document.
/// Bodies are not copied into the index; they are read lazily from <see cref="HarEntry.Raw"/>.
/// </summary>
public sealed class HarCapture
{
    public required string CaptureId { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required FileFingerprint Fingerprint { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }
    public string? HarVersion { get; init; }
    public string? Creator { get; init; }
    public string? Browser { get; init; }
    public required IReadOnlyList<HarEntry> Entries { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Keeps the parsed document (and the file bytes it references) alive.</summary>
    internal JsonDocument? Document { get; init; }

    public DateTimeOffset? FirstStart => Entries.Where(e => e.Started is not null).Min(e => e.Started);

    public DateTimeOffset? LastEnd => Entries
        .Where(e => e.Started is not null)
        .Max(e => e.Started!.Value.AddMilliseconds(e.TimeMs ?? 0));

    public string EntryId(int number) => $"{CaptureId}:entry-{number}";
}

/// <summary>Identity of a file's content, used to detect changes and to version entry IDs.</summary>
public sealed record FileFingerprint(long Length, DateTime LastWriteUtc, string Sha256Prefix)
{
    /// <summary>Short content hash, exposed to callers as the capture "revision".</summary>
    public string Revision => Sha256Prefix;
}
