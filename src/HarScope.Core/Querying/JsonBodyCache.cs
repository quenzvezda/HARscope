using System.Text.Json;
using HarScope.Core.Models;

namespace HarScope.Core.Querying;

/// <summary>A decoded body plus its parsed JSON root (null with an error when it is not JSON).</summary>
public sealed record ParsedBody(BodyContent Body, JsonElement? Root, string? Error);

/// <summary>
/// Small LRU cache of parsed JSON bodies, so repeated har_query_body / compare calls on the same
/// entry do not re-decode and re-parse it. Keyed by capture revision, so a reloaded file never hits stale data.
/// </summary>
public sealed class JsonBodyCache
{
    private readonly record struct Key(string CaptureId, string Revision, int Number, BodySide Side);

    private readonly int _maxEntries;
    private readonly long _maxChars;
    private readonly Lock _lock = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, ParsedBody Value, long Chars)>> _map = new();
    private readonly LinkedList<(Key Key, ParsedBody Value, long Chars)> _lru = new();
    private long _chars;

    public JsonBodyCache(int maxEntries = 64, long maxChars = 64L * 1024 * 1024)
    {
        _maxEntries = maxEntries;
        _maxChars = maxChars;
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public ParsedBody Get(HarCapture capture, HarEntry entry, BodySide side, int maxParamValueChars)
    {
        var key = new Key(capture.CaptureId, capture.Fingerprint.Revision, entry.Number, side);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
        }

        var parsed = Parse(BodyReader.Read(entry, side, maxParamValueChars));
        var chars = parsed.Body.Text?.Length ?? 0;
        if (chars > _maxChars) return parsed; // too large to keep

        lock (_lock)
        {
            if (_map.ContainsKey(key)) return parsed;
            _map[key] = _lru.AddFirst((key, parsed, chars));
            _chars += chars;
            while (_map.Count > _maxEntries || _chars > _maxChars)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _chars -= last.Value.Chars;
            }
        }
        return parsed;
    }

    private static ParsedBody Parse(BodyContent body)
    {
        if (body.Text is null)
            return new ParsedBody(body, null, body.Reason ?? (body.Params is not null
                ? "Body is form parameters, not JSON; use har_get_entry with sections=['requestBody']."
                : "Body is not available."));

        try
        {
            using var doc = JsonDocument.Parse(body.Text, new JsonDocumentOptions { MaxDepth = 256, AllowTrailingCommas = true });
            return new ParsedBody(body, doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return new ParsedBody(body, null,
                $"Body is not valid JSON (mime {body.MimeType ?? "unknown"}; line {ex.LineNumber + 1}). " +
                "Use har_get_entry with a body section to read it as text.");
        }
    }
}
