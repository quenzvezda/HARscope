using System.Security.Cryptography;
using System.Text;
using HarScope.Core.Models;
using HarScope.Core.Parsing;
using HarScope.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HarScope.Core.Indexing;

public sealed record HarFileInfo(string Path, long SizeBytes, DateTimeOffset LastModified, string? CaptureId, bool Loaded, bool Active);

/// <summary>A capture plus notes produced while resolving it (e.g. "file changed, reloaded").</summary>
public sealed record CaptureHandle(HarCapture Capture, IReadOnlyList<string> Warnings);

/// <summary>
/// In-memory store of loaded captures. Keeps at most <see cref="HarScopeOptions.MaxOpenCaptures"/> captures,
/// remembers which file each capture ID belongs to (so evicted captures reload transparently),
/// and reloads a capture when its file changes on disk.
/// </summary>
public sealed class CaptureStore
{
    private readonly HarScopeOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, HarCapture> _loaded = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, string> _idToPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pathToId = new(StringComparer.OrdinalIgnoreCase);
    private PathGuard? _guard;
    private string? _activeId;

    public CaptureStore(HarScopeOptions options, ILogger<CaptureStore>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<CaptureStore>.Instance;
    }

    public PathGuard Guard => _guard ??= new PathGuard(_options.RootPath ?? "");

    public IReadOnlyList<HarFileInfo> List(out bool truncated)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            // Do not follow links while listing: they could point outside the root.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };

        var files = Directory.EnumerateFiles(Guard.Root, "*.har", enumeration)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(_options.MaxListedFiles + 1)
            .ToList();

        truncated = files.Count > _options.MaxListedFiles;
        lock (_lock)
        {
            return files.Take(_options.MaxListedFiles).Select(f =>
            {
                var rel = Guard.ToRelative(f.FullName);
                _pathToId.TryGetValue(rel, out var id);
                return new HarFileInfo(rel, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), id,
                    id is not null && _loaded.ContainsKey(id), id is not null && id == _activeId);
            }).ToList();
        }
    }

    /// <summary>Loads (or reuses) a file and makes it the active capture.</summary>
    public CaptureHandle Open(string relativePath)
    {
        var full = Guard.Resolve(relativePath);
        var rel = Guard.ToRelative(full);
        lock (_lock)
        {
            var id = IdFor(rel);
            var handle = GetOrLoad(id, rel);
            _activeId = id;
            return handle;
        }
    }

    /// <summary>Returns the given capture, or the active one when <paramref name="captureId"/> is null.</summary>
    public CaptureHandle Get(string? captureId)
    {
        lock (_lock)
        {
            var id = string.IsNullOrWhiteSpace(captureId) ? _activeId : captureId.Trim();
            if (id is null)
                throw new HarScopeException(ErrorCodes.NoActiveCapture, "No capture is open. Call har_open first.");
            if (!_idToPath.TryGetValue(id, out var rel))
                throw new HarScopeException(ErrorCodes.UnknownCapture, $"Unknown capture '{id}'. Call har_list / har_open first.");
            return GetOrLoad(id, rel);
        }
    }

    /// <summary>Resolves an entry ID of the form "&lt;captureId&gt;:entry-&lt;n&gt;".</summary>
    public (CaptureHandle Handle, HarEntry Entry) GetEntry(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            throw new HarScopeException(ErrorCodes.InvalidArgument, "entryId is required.");

        var marker = entryId.LastIndexOf(":entry-", StringComparison.Ordinal);
        if (marker <= 0 || !int.TryParse(entryId.AsSpan(marker + 7), out var number))
            throw new HarScopeException(ErrorCodes.InvalidArgument,
                $"Malformed entryId '{entryId}'. Expected '<captureId>:entry-<number>'.");

        var handle = Get(entryId[..marker]);
        var entry = FindByNumber(handle.Capture.Entries, number)
                    ?? throw new HarScopeException(ErrorCodes.UnknownEntry,
                        $"Entry {number} does not exist in capture '{handle.Capture.CaptureId}' ({handle.Capture.Entries.Count} entries).");
        return (handle, entry);
    }

    private static HarEntry? FindByNumber(IReadOnlyList<HarEntry> entries, int number)
    {
        // Entries are stored in original order; numbers only have gaps when malformed entries were skipped.
        if (number >= 1 && number <= entries.Count && entries[number - 1].Number == number) return entries[number - 1];
        var lo = 0;
        var hi = entries.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var n = entries[mid].Number;
            if (n == number) return entries[mid];
            if (n < number) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    private CaptureHandle GetOrLoad(string id, string rel)
    {
        var warnings = new List<string>();
        var full = Path.Combine(Guard.Root, rel);

        if (_loaded.TryGetValue(id, out var existing))
        {
            var file = new FileInfo(full);
            if (!file.Exists)
            {
                warnings.Add("The source file no longer exists; serving the copy loaded earlier.");
                Touch(id);
                return new CaptureHandle(existing, warnings);
            }

            if (file.Length == existing.Fingerprint.Length && file.LastWriteTimeUtc == existing.Fingerprint.LastWriteUtc)
            {
                Touch(id);
                return new CaptureHandle(existing, warnings);
            }

            var reloaded = Load(id, rel, Guard.Resolve(rel));
            if (reloaded.Fingerprint.Revision != existing.Fingerprint.Revision)
            {
                warnings.Add($"File changed on disk and was reloaded (revision {existing.Fingerprint.Revision} -> " +
                             $"{reloaded.Fingerprint.Revision}). Entry IDs obtained earlier may now point to different requests.");
                _logger.LogInformation("Reloaded capture {CaptureId} after file change", id);
            }
            Store(id, reloaded);
            return new CaptureHandle(reloaded, warnings);
        }

        var capture = Load(id, rel, Guard.Resolve(rel));
        Store(id, capture);
        return new CaptureHandle(capture, warnings);
    }

    private HarCapture Load(string id, string rel, string full)
    {
        var info = new FileInfo(full);
        if (info.Length > _options.MaxFileBytes)
            throw new HarScopeException(ErrorCodes.FileTooLarge,
                $"File is {info.Length / (1024 * 1024)} MB; the limit is {_options.MaxFileBytes / (1024 * 1024)} MB (HARSCOPE_MAX_FILE_MB).");

        var bytes = File.ReadAllBytes(full);
        var fingerprint = new FileFingerprint(info.Length, info.LastWriteTimeUtc,
            Convert.ToHexStringLower(SHA256.HashData(bytes), 0, 6));

        var capture = HarParser.Parse(bytes, id, rel, full, fingerprint);
        _logger.LogInformation("Loaded capture {CaptureId}: {Count} entries, {Bytes} bytes", id, capture.Entries.Count, bytes.Length);
        return capture;
    }

    private void Store(string id, HarCapture capture)
    {
        // Old documents are not disposed explicitly: a concurrent reader may still hold an element.
        // Without disposal the GC reclaims them normally.
        _loaded[id] = capture;
        Touch(id);
        while (_loaded.Count > Math.Max(1, _options.MaxOpenCaptures))
        {
            var oldest = _lru.Last!.Value;
            _lru.RemoveLast();
            _loaded.Remove(oldest);
            _logger.LogInformation("Evicted capture {CaptureId} from memory", oldest);
        }
    }

    private void Touch(string id)
    {
        _lru.Remove(id);
        _lru.AddFirst(id);
    }

    /// <summary>Capture IDs are slugs of the relative path, made unique with a numeric suffix.</summary>
    private string IdFor(string rel)
    {
        if (_pathToId.TryGetValue(rel, out var existing)) return existing;

        var baseSlug = Slug(rel);
        var slug = baseSlug;
        for (var i = 2; _idToPath.ContainsKey(slug); i++) slug = $"{baseSlug}-{i}";

        _pathToId[rel] = slug;
        _idToPath[slug] = rel;
        return slug;
    }

    public static string Slug(string relativePath)
    {
        var withoutExt = relativePath.EndsWith(".har", StringComparison.OrdinalIgnoreCase)
            ? relativePath[..^4]
            : relativePath;

        var sb = new StringBuilder(withoutExt.Length);
        foreach (var ch in withoutExt.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return slug.Length == 0 ? "capture" : slug;
    }
}
