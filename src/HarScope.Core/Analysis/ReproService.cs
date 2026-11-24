using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarScope.Core.Indexing;
using HarScope.Core.Models;
using HarScope.Core.Parsing;
using HarScope.Core.Querying;
using HarScope.Core.Security;

namespace HarScope.Core.Analysis;

public enum ReproFormat { Http, Curl }

public sealed record ReproVariable(string Name, string Source);

public sealed record ReproRequest(string Id, string Title, string Text, IReadOnlyList<string>? Notes);

/// <summary>A value in a request that was first seen in an earlier response (e.g. a token or an order ID).</summary>
public sealed record ReproDependency(string Entry, string UsedIn, string Value, string ProducedBy, string ProducedAt);

public sealed record ReproResult
{
    public required string Format { get; init; }
    public required IReadOnlyList<ReproRequest> Requests { get; init; }
    public IReadOnlyList<ReproVariable>? Variables { get; init; }
    public IReadOnlyList<ReproDependency>? Dependencies { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Builds sanitized request templates (REST Client .http or bash cURL) from recorded entries.
/// Nothing is sent. Secrets become {{placeholders}}; the caller fills them in.
/// </summary>
public sealed class ReproService(HarScopeOptions options, Redactor redactor, JsonBodyCache cache)
{
    public const int MaxEntries = 10;
    private const int MaxBodyChars = 20_000;
    private const int MaxDependencies = 30;
    private const int MaxProducerEntries = 300;

    private static readonly HashSet<string> DropHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "content-length", "accept-encoding", "keep-alive", "transfer-encoding", "upgrade",
        "te", "priority", "pragma", "cache-control", "dnt",
    };

    /// <summary>Headers kept in minimal mode (besides custom x-* headers and secret placeholders).</summary>
    private static readonly HashSet<string> MinimalHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "accept", "accept-language",
    };

    private static readonly string[] SecretBodyKeys =
        ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "otp", "pin", "credential", "privatekey"];

    public ReproResult Build(IReadOnlyList<(CaptureHandle Handle, HarEntry Entry)> items, ReproFormat format, bool allHeaders)
    {
        if (items.Count == 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "At least one entryId is required.");
        if (items.Count > MaxEntries)
            throw new HarScopeException(ErrorCodes.InvalidArgument, $"At most {MaxEntries} entries per call.");

        var ordered = items.OrderBy(i => i.Entry.Started is null).ThenBy(i => i.Entry.Started).ThenBy(i => i.Entry.Number).ToList();
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requests = ordered.Select(i => Render(i.Entry, format, allHeaders, variables)).ToList();

        var warnings = new List<string>
        {
            "Templates only; nothing was sent. A recorded request may not be reproducible: sessions, CSRF tokens, " +
            "cookies and server state may have changed. Fill the {{placeholders}} with current values.",
            "Secret header/cookie/query values and secret-looking JSON body fields were replaced by placeholders; " +
            "other body values are copied as recorded and may still contain personal data.",
        };
        if (format == ReproFormat.Curl)
            warnings.Add("cURL commands use bash quoting. In PowerShell call curl.exe and adjust quoting, or use format='http'.");

        return new ReproResult
        {
            Format = format == ReproFormat.Http ? "http" : "curl",
            Requests = requests,
            Variables = variables.Count > 0 ? variables.Select(v => new ReproVariable(v.Key, v.Value)).ToList() : null,
            Dependencies = Dependencies(ordered) is { Count: > 0 } d ? d : null,
            Warnings = warnings,
        };
    }

    private ReproRequest Render(HarEntry e, ReproFormat format, bool allHeaders, Dictionary<string, string> variables)
    {
        var notes = new List<string>();
        var request = Json.Object(e.Raw, "request");
        var url = SanitizeUrl(e.Url, variables);

        var headers = new List<(string Name, string Value)>();
        var hadCookie = false;
        foreach (var h in Json.Array(request, "headers"))
        {
            var name = Json.String(h, "name") ?? "";
            var value = Json.String(h, "value") ?? "";
            if (name.Length == 0 || name.StartsWith(':') || DropHeaders.Contains(name)) continue;
            if (name.Equals("cookie", StringComparison.OrdinalIgnoreCase)) { hadCookie = true; continue; }

            if (redactor.IsSensitiveHeader(name) || Redactor.LooksLikeSecret(value))
            {
                var variable = VarName(name);
                variables.TryAdd(variable, $"request header '{name}' (recorded value withheld)");
                headers.Add((name, value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? $"Bearer {{{{{variable}}}}}"
                    : $"{{{{{variable}}}}}"));
                continue;
            }

            if (!allHeaders)
            {
                var keep = MinimalHeaders.Contains(name) ||
                           (name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) && !CompareService.VolatileHeaders.Contains(name));
                if (!keep) continue;
            }
            headers.Add((name, value));
        }

        // Cookie header and/or HAR cookie list -> one placeholder.
        if (hadCookie || Json.Array(request, "cookies").Any())
        {
            variables.TryAdd("cookie", "request Cookie header (recorded value withheld)");
            headers.Add(("Cookie", "{{cookie}}"));
        }

        var body = BodyReader.Request(e, options.MaxValueChars);
        string? bodyText = null;
        if (body.Text is { } text)
        {
            bodyText = SanitizeBody(text, body.MimeType, variables);
            if (bodyText.Length > MaxBodyChars)
            {
                bodyText = bodyText[..MaxBodyChars];
                notes.Add($"Body truncated to {MaxBodyChars} chars.");
            }
            if (!headers.Any(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) && body.MimeType is { } mt)
                headers.Add(("Content-Type", mt));
        }
        else if (body.Params is { Count: > 0 })
        {
            notes.Add("Form/multipart parameters are not rendered; files are not available in the HAR.");
        }

        if (e.Status == 0) notes.Add("The recorded request got no response.");

        var title = $"{e.Id} {e.Method} {e.Path}";
        var rendered = format == ReproFormat.Http
            ? RenderHttp(title, e.Method, url, headers, bodyText)
            : RenderCurl(e.Method, url, headers, bodyText);
        return new ReproRequest(e.Id, title, rendered, notes.Count > 0 ? notes : null);
    }

    private static string RenderHttp(string title, string method, string url, List<(string Name, string Value)> headers, string? body)
    {
        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(title);
        sb.Append(method).Append(' ').AppendLine(url);
        foreach (var (n, v) in headers) sb.Append(n).Append(": ").AppendLine(v);
        if (body is not null) sb.AppendLine().AppendLine(body);
        return sb.ToString().TrimEnd();
    }

    private static string RenderCurl(string method, string url, List<(string Name, string Value)> headers, string? body)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder("curl");
        if (method != "GET" || body is not null) sb.Append(" -X ").Append(method);
        sb.Append(' ').Append(Q(url));
        foreach (var (n, v) in headers) sb.Append(" \\\n  -H ").Append(Q($"{n}: {v}"));
        if (body is not null) sb.Append(" \\\n  --data-raw ").Append(Q(body));
        return sb.ToString();
    }

    private string SanitizeUrl(string url, Dictionary<string, string> variables)
    {
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var hash = url.IndexOf('#', q);
        var query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0) continue;
            var name = Uri.UnescapeDataString(parts[i][..eq]);
            var value = Uri.UnescapeDataString(parts[i][(eq + 1)..]);
            if (!redactor.IsSensitiveQueryParam(name) && !Redactor.LooksLikeSecret(value)) continue;
            var variable = VarName(name);
            variables.TryAdd(variable, $"query parameter '{name}' (recorded value withheld)");
            parts[i] = $"{parts[i][..eq]}={{{{{variable}}}}}";
        }
        return url[..(q + 1)] + string.Join('&', parts) + (hash < 0 ? "" : url[hash..]);
    }

    private string SanitizeBody(string text, string? mime, Dictionary<string, string> variables)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(text);
                if (node is not null)
                {
                    ReplaceSecrets(node, variables);
                    return node.ToJsonString(HarJson.Options);
                }
            }
            catch (JsonException)
            {
                // fall through: not JSON
            }
        }

        if (mime?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            var parts = text.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var eq = parts[i].IndexOf('=');
                if (eq < 0) continue;
                var name = Uri.UnescapeDataString(parts[i][..eq].Replace('+', ' '));
                if (!IsSecretKey(name) && !redactor.IsSensitiveQueryParam(name)) continue;
                var variable = VarName(name);
                variables.TryAdd(variable, $"form field '{name}' (recorded value withheld)");
                parts[i] = $"{parts[i][..eq]}={{{{{variable}}}}}";
            }
            return string.Join('&', parts);
        }

        return text;
    }

    private static void ReplaceSecrets(JsonNode node, Dictionary<string, string> variables)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue && IsSecretKey(key))
                    {
                        var variable = VarName(key);
                        variables.TryAdd(variable, $"JSON body field '{key}' (recorded value withheld)");
                        obj[key] = $"{{{{{variable}}}}}";
                    }
                    else if (child is not null) ReplaceSecrets(child, variables);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr) if (child is not null) ReplaceSecrets(child, variables);
                break;
        }
    }

    private static bool IsSecretKey(string key)
    {
        var k = key.Replace("_", "").Replace("-", "");
        return SecretBodyKeys.Any(s => k.Contains(s.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
    }

    private static string VarName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_') is { Length: > 0 } v ? v : "value";
    }

    // ---------- dependencies ----------

    private List<ReproDependency> Dependencies(List<(CaptureHandle Handle, HarEntry Entry)> items)
    {
        var result = new List<ReproDependency>();
        foreach (var (handle, entry) in items)
        {
            var producers = Producers(handle, entry);
            if (producers.Count == 0) continue;

            foreach (var (where, value) in Consumed(handle.Capture, entry))
            {
                if (!producers.TryGetValue(value, out var source)) continue;
                var secret = Redactor.LooksLikeSecret(value) || where.StartsWith("header:", StringComparison.Ordinal) ||
                             value.Length > 40;
                result.Add(new ReproDependency(entry.Id, where, secret ? redactor.Mask(value) : value, source.Id, source.Path));
                if (result.Count >= MaxDependencies) return result;
            }
        }
        return result;
    }

    /// <summary>String/number leaves (length >= 6) of JSON responses recorded before the entry.</summary>
    private Dictionary<string, (string Id, string Path)> Producers(CaptureHandle handle, HarEntry entry)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var earlier = handle.Capture.Entries
            .Where(e => e.Started is not null && entry.Started is not null && e.Started < entry.Started && e.HasResponseBody)
            .OrderByDescending(e => e.Started)
            .Take(MaxProducerEntries);

        foreach (var e in earlier)
        {
            var parsed = cache.Get(handle.Capture, e, BodySide.Response, options.MaxValueChars);
            if (parsed.Root is not { } root) continue;
            foreach (var (path, value) in Leaves("$", root))
                map.TryAdd(value, (e.Id, path)); // most recent producer wins (iterating newest first)
        }
        return map;
    }

    private IEnumerable<(string Where, string Value)> Consumed(HarCapture capture, HarEntry entry)
    {
        var request = Json.Object(entry.Raw, "request");

        foreach (var q in Json.Array(request, "queryString"))
            if (Json.String(q, "value") is { Length: >= 6 } v) yield return ($"query:{Json.String(q, "name")}", v);

        foreach (var h in Json.Array(request, "headers"))
        {
            var name = Json.String(h, "name") ?? "";
            var value = Json.String(h, "value") ?? "";
            if (name.StartsWith(':') || value.Length < 6) continue;
            // Only headers that can carry application state; content-type & co. would match by accident.
            if (!redactor.IsSensitiveHeader(name) && !Redactor.LooksLikeSecret(value) &&
                !name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) continue;
            var token = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : value;
            yield return ($"header:{name.ToLowerInvariant()}", token);
        }

        var body = cache.Get(capture, entry, BodySide.Request, options.MaxValueChars);
        if (body.Root is { } root)
            foreach (var (path, value) in Leaves("$", root))
                yield return ($"body:{path}", value);
    }

    private static IEnumerable<(string Path, string Value)> Leaves(string path, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                foreach (var leaf in Leaves($"{path}.{p.Name}", p.Value))
                    yield return leaf;
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (i >= 200) yield break;
                    foreach (var leaf in Leaves($"{path}[{i++}]", item)) yield return leaf;
                }
                break;
            case JsonValueKind.String:
                if (e.GetString() is { Length: >= 6 } s) yield return (path, s);
                break;
            case JsonValueKind.Number:
                if (e.GetRawText() is { Length: >= 6 } n && !n.Contains('.')) yield return (path, n);
                break;
        }
    }
}
