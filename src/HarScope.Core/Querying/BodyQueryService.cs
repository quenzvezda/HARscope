using System.Globalization;
using System.Text.Json;
using HarScope.Core.Indexing;
using HarScope.Core.Models;

namespace HarScope.Core.Querying;

public enum BodyQueryMode { Values, Structure }

public enum BodyAggregate { None, Count, Sum, Min, Max, Avg, Distinct }

public sealed record BodyQuery
{
    public BodySide Side { get; init; } = BodySide.Response;
    public string? Path { get; init; }
    public BodyQueryMode Mode { get; init; } = BodyQueryMode.Values;
    /// <summary>Relative paths projected from every match (e.g. ["errorCode", "detail.message"]).</summary>
    public IReadOnlyList<string>? Fields { get; init; }
    /// <summary>Row filters ("field op value"), combined with AND, applied to every match before anything else.</summary>
    public IReadOnlyList<string>? Where { get; init; }
    public BodyAggregate Aggregate { get; init; } = BodyAggregate.None;
    /// <summary>Relative path inside each match whose value is aggregated (default: the match itself).</summary>
    public string? ValueField { get; init; }
    /// <summary>Relative path inside each match used to group the aggregate.</summary>
    public string? GroupBy { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }
}

public sealed record MatchedValue(string Path, object? Value);

public sealed record AggregateGroup(object? Key, decimal? Value, int Count);

public sealed record AggregateResult(
    string Function,
    decimal? Value,
    int Count,
    int Skipped,
    int? TotalGroups,
    IReadOnlyList<AggregateGroup>? Groups,
    bool GroupsTruncated);

public sealed record BodyQueryResult
{
    public required string EntryId { get; init; }
    public required string Source { get; init; }
    public required string Path { get; init; }
    public required bool Available { get; init; }
    public string? Reason { get; init; }
    public string? MimeType { get; init; }
    public int? Matched { get; init; }
    /// <summary>Matches before the where filter (only set when a filter was given).</summary>
    public int? Scanned { get; init; }
    public int? Offset { get; init; }
    public int? ReturnedCount { get; init; }
    public bool? HasMore { get; init; }
    public int? NextOffset { get; init; }
    public IReadOnlyList<MatchedValue>? Values { get; init; }
    public object? Structure { get; init; }
    public AggregateResult? Aggregate { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>
/// Level 4: targeted access to JSON bodies — single values, projections, structure previews and
/// simple aggregates — so the caller never needs to page through a large body.
/// </summary>
public sealed class BodyQueryService
{
    private readonly HarScopeOptions options;
    private readonly JsonBodyCache cache;

    public BodyQueryService(HarScopeOptions options, JsonBodyCache cache)
    {
        this.options = options;
        this.cache = cache;
    }

    public BodyQueryService(HarScopeOptions options) : this(options, new JsonBodyCache()) { }

    private const int DefaultLimit = 20;
    private const int MaxLimit = 200;
    private const int MaxGroups = 100;
    private const int MaxValueJsonChars = 2_000;
    private const int MaxStructureDepth = 6;
    private const int MaxStructureKeys = 60;
    private const int StructureSample = 25;

    public BodyQueryResult Query(CaptureHandle handle, HarEntry entry, BodyQuery query)
    {
        var path = JsonPath.Parse(query.Path);
        var where = query.Where?.Where(w => !string.IsNullOrWhiteSpace(w)).Select(WhereClause.Parse).ToList();
        Validate(query);

        var source = query.Side == BodySide.Request ? "request" : "response";
        var warnings = new List<string>(handle.Warnings);
        var parsed = cache.Get(handle.Capture, entry, query.Side, options.MaxValueChars);
        var body = parsed.Body;

        BodyQueryResult Unavailable(string reason) => new()
        {
            EntryId = entry.Id, Source = source, Path = path.Text, Available = false, Reason = reason, MimeType = body.MimeType,
            Warnings = warnings.Count > 0 ? warnings : null,
        };

        if (parsed.Root is not { } root) return Unavailable(parsed.Error!);

        {
            var matches = path.Evaluate(root, out var capped);
            if (capped) warnings.Add($"Path matched more than {JsonPath.MaxMatches} nodes; only the first {JsonPath.MaxMatches} were used.");
            int? scanned = null;
            if (where is { Count: > 0 })
            {
                scanned = matches.Count;
                matches = matches.Where(m => where.All(w => w.Matches(m.Value))).ToList();
            }
            if (matches.Count == 0)
                warnings.Add(scanned is > 0
                    ? $"None of the {scanned} matches passed the where filter."
                    : "Path matched nothing. Try mode='structure' with a shorter path to see what exists.");

            var result = new BodyQueryResult
            {
                EntryId = entry.Id, Source = source, Path = path.Text, Available = true, MimeType = body.MimeType,
                Matched = matches.Count,
                Scanned = scanned,
            };

            if (query.Mode == BodyQueryMode.Structure)
            {
                return Fit(result with
                {
                    Structure = matches.Count == 0 ? null : DescribeAll(matches.Select(m => m.Value)),
                    Warnings = warnings.Count > 0 ? warnings : null,
                });
            }

            if (query.Aggregate != BodyAggregate.None)
            {
                return Fit(result with
                {
                    Aggregate = Aggregate(matches, query),
                    Warnings = warnings.Count > 0 ? warnings : null,
                });
            }

            var fields = query.Fields?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => (Name: f.Trim(), Path: JsonPath.Parse(f))).ToList();
            var limit = Math.Min(query.Limit ?? DefaultLimit, MaxLimit);
            var offset = query.Offset;
            var truncatedValues = false;

            var page = matches.Skip(offset).Take(limit).Select(m =>
            {
                object? value = fields is { Count: > 0 }
                    ? fields.ToDictionary(f => f.Name, f => Project(m.Value, f.Path, ref truncatedValues))
                    : Render(m.Value, ref truncatedValues);
                return new MatchedValue(m.Path, value);
            }).ToList();

            if (truncatedValues)
                warnings.Add($"Values larger than {MaxValueJsonChars} chars were replaced by a summary; query a deeper path, " +
                             "use fields, or mode='structure'.");

            return FitPage(result, page, offset, matches.Count, truncatedValues, warnings);
        }
    }

    private static void Validate(BodyQuery q)
    {
        if (q.Offset < 0) throw new HarScopeException(ErrorCodes.InvalidArgument, "offset must be >= 0.");
        if (q.Limit is < 1) throw new HarScopeException(ErrorCodes.InvalidArgument, "limit must be >= 1.");
        if (q.Aggregate == BodyAggregate.None && (q.GroupBy is not null || q.ValueField is not null))
            throw new HarScopeException(ErrorCodes.InvalidArgument, "groupBy / valueField require an aggregate.");
        if (q.Aggregate != BodyAggregate.None && q.Fields is { Count: > 0 })
            throw new HarScopeException(ErrorCodes.InvalidArgument, "fields cannot be combined with an aggregate; use valueField / groupBy.");
        if (q.Mode == BodyQueryMode.Structure && (q.Aggregate != BodyAggregate.None || q.Fields is { Count: > 0 }))
            throw new HarScopeException(ErrorCodes.InvalidArgument, "mode='structure' cannot be combined with fields or an aggregate.");
    }

    // ---------- values ----------

    private static object? Project(JsonElement item, JsonPath field, ref bool truncated)
    {
        var found = field.Evaluate(item, out _);
        return found.Count switch
        {
            0 => null,
            1 => Render(found[0].Value, ref truncated),
            _ => Render(found.Select(f => f.Value).ToList(), ref truncated),
        };
    }

    private static object? Render(List<JsonElement> values, ref bool truncated)
    {
        var list = new List<object?>(values.Count);
        foreach (var v in values) list.Add(Render(v, ref truncated));
        return list;
    }

    /// <summary>Returns the value itself when small, otherwise a summary of its shape.</summary>
    private static object? Render(JsonElement value, ref bool truncated)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                var s = value.GetString()!;
                if (s.Length <= MaxValueJsonChars) return s;
                truncated = true;
                return s[..MaxValueJsonChars] + $"…[+{s.Length - MaxValueJsonChars} chars]";
            case JsonValueKind.Object or JsonValueKind.Array:
                var raw = value.GetRawText();
                if (raw.Length <= MaxValueJsonChars) return value.Clone();
                truncated = true;
                return value.ValueKind == JsonValueKind.Array
                    ? new Dictionary<string, object?> { ["_summary"] = "array", ["length"] = value.GetArrayLength(), ["chars"] = raw.Length }
                    : new Dictionary<string, object?>
                    {
                        ["_summary"] = "object",
                        ["keys"] = value.EnumerateObject().Select(p => p.Name).Take(MaxStructureKeys).ToList(),
                        ["chars"] = raw.Length,
                    };
            default:
                return value.Clone();
        }
    }

    private BodyQueryResult FitPage(BodyQueryResult result, List<MatchedValue> page, int offset, int total, bool truncated,
        List<string> warnings)
    {
        var shortened = false;
        BodyQueryResult Build() => result with
        {
            Offset = offset,
            ReturnedCount = page.Count,
            HasMore = offset + page.Count < total,
            NextOffset = offset + page.Count < total ? offset + page.Count : null,
            Values = page,
            Truncated = truncated || shortened,
            Warnings = warnings.Count > 0 ? warnings : null,
        };

        var built = Build();
        while (page.Count > 1 && HarJson.Serialize(built).Length > options.MaxOutputChars)
        {
            page.RemoveRange(page.Count - Math.Max(1, page.Count / 4), Math.Max(1, page.Count / 4));
            if (!shortened)
            {
                shortened = true;
                warnings.Add("Page shortened to stay within the output limit; continue with nextOffset or use fields.");
            }
            built = Build();
        }
        return built;
    }

    private BodyQueryResult Fit(BodyQueryResult result)
    {
        if (HarJson.Serialize(result).Length <= options.MaxOutputChars) return result;
        var warnings = (result.Warnings ?? []).Append("Result exceeded the output limit and was dropped; narrow the path.").ToList();
        return result with { Structure = null, Aggregate = null, Truncated = true, Warnings = warnings };
    }

    // ---------- aggregates ----------

    private static AggregateResult Aggregate(List<(string Path, JsonElement Value)> matches, BodyQuery q)
    {
        var valuePath = q.ValueField is { Length: > 0 } vf ? JsonPath.Parse(vf) : null;
        var groupPath = q.GroupBy is { Length: > 0 } gb ? JsonPath.Parse(gb) : null;
        var function = q.Aggregate.ToString().ToLowerInvariant();

        JsonElement? ValueOf(JsonElement m) =>
            valuePath is null ? m : valuePath.Evaluate(m, out _) is { Count: > 0 } found ? found[0].Value : null;

        // distinct == count grouped by the value itself.
        if (q.Aggregate == BodyAggregate.Distinct)
        {
            groupPath ??= valuePath;
            valuePath = null;
        }

        if (groupPath is null && q.Aggregate != BodyAggregate.Distinct)
        {
            var (value, count, skipped) = Compute(q.Aggregate, matches.Select(m => ValueOf(m.Value)));
            return new AggregateResult(function, value, count, skipped, null, null, false);
        }

        var groups = matches
            .GroupBy(m => groupPath is null ? KeyOf(m.Value) : groupPath.Evaluate(m.Value, out _) is { Count: > 0 } g ? KeyOf(g[0].Value) : "(missing)")
            .Select(g =>
            {
                var (value, count, _) = q.Aggregate is BodyAggregate.Count or BodyAggregate.Distinct
                    ? (g.Count(), g.Count(), 0)
                    : Compute(q.Aggregate, g.Select(m => ValueOf(m.Value)));
                return new AggregateGroup(g.Key, value, count);
            })
            .OrderByDescending(g => g.Value ?? decimal.MinValue)
            .ThenBy(g => g.Key?.ToString(), StringComparer.Ordinal)
            .ToList();

        var (total, totalCount, skippedTotal) = q.Aggregate is BodyAggregate.Count or BodyAggregate.Distinct
            ? (matches.Count, matches.Count, 0)
            : Compute(q.Aggregate, matches.Select(m => ValueOf(m.Value)));

        return new AggregateResult(function, total, totalCount, skippedTotal, groups.Count,
            groups.Take(MaxGroups).ToList(), groups.Count > MaxGroups);
    }

    /// <summary>Returns (value, numeric count used, skipped non-numeric).</summary>
    private static (decimal? Value, int Count, int Skipped) Compute(BodyAggregate fn, IEnumerable<JsonElement?> values)
    {
        if (fn == BodyAggregate.Count)
        {
            var all = values.ToList();
            return (all.Count, all.Count, 0);
        }

        var numbers = new List<decimal>();
        var skipped = 0;
        foreach (var v in values)
        {
            if (v is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d)) numbers.Add(d);
            else skipped++;
        }

        if (numbers.Count == 0) return (null, 0, skipped);
        decimal result = fn switch
        {
            BodyAggregate.Sum => numbers.Sum(),
            BodyAggregate.Min => numbers.Min(),
            BodyAggregate.Max => numbers.Max(),
            BodyAggregate.Avg => Math.Round(numbers.Average(), 6),
            _ => numbers.Count,
        };
        return (result, numbers.Count, skipped);
    }

    private static object? KeyOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : value.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText() is { Length: > 100 } raw ? raw[..100] + "…" : value.GetRawText(),
    };

    // ---------- structure ----------

    private static object? DescribeAll(IEnumerable<JsonElement> values)
    {
        object? merged = null;
        foreach (var v in values.Take(StructureSample)) merged = merged is null ? Describe(v, 0) : Merge(merged, Describe(v, 0));
        return merged;
    }

    /// <summary>
    /// Shape without values: scalars become "string" / "number" / "boolean" / "null";
    /// objects list their keys; arrays give their length and the merged shape of up to 25 sampled items.
    /// </summary>
    private static object Describe(JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= MaxStructureDepth) return $"object({value.EnumerateObject().Count()} keys)";
                var keys = new Dictionary<string, object>();
                var extra = 0;
                foreach (var p in value.EnumerateObject())
                {
                    if (keys.Count >= MaxStructureKeys) { extra++; continue; }
                    keys[p.Name] = Describe(p.Value, depth + 1);
                }
                var obj = new Dictionary<string, object> { ["type"] = "object", ["keys"] = keys };
                if (extra > 0) obj["moreKeys"] = extra;
                return obj;

            case JsonValueKind.Array:
                var length = value.GetArrayLength();
                var arr = new Dictionary<string, object> { ["type"] = "array", ["length"] = length };
                if (length > 0 && depth < MaxStructureDepth)
                {
                    object? items = null;
                    foreach (var item in value.EnumerateArray().Take(StructureSample))
                        items = items is null ? Describe(item, depth + 1) : Merge(items, Describe(item, depth + 1));
                    arr["items"] = items!;
                }
                return arr;

            case JsonValueKind.String: return "string";
            case JsonValueKind.Number: return "number";
            case JsonValueKind.True or JsonValueKind.False: return "boolean";
            default: return "null";
        }
    }

    private static object Merge(object a, object b)
    {
        if (a is string sa && b is string sb) return MergeNames(sa, sb);

        if (a is Dictionary<string, object> da && b is Dictionary<string, object> db &&
            Equals(da["type"], db["type"]))
        {
            var result = new Dictionary<string, object>(da);
            if ((string)da["type"] == "object")
            {
                var ka = (Dictionary<string, object>)da["keys"];
                var kb = (Dictionary<string, object>)db["keys"];
                var keys = new Dictionary<string, object>(ka);
                foreach (var (k, v) in kb) keys[k] = keys.TryGetValue(k, out var existing) ? Merge(existing, v) : v;
                result["keys"] = keys;
            }
            else
            {
                result["length"] = Math.Max((int)da["length"], (int)db["length"]);
                if (da.TryGetValue("items", out var ia) && db.TryGetValue("items", out var ib)) result["items"] = Merge(ia, ib);
                else if (db.TryGetValue("items", out var only)) result["items"] = only;
            }
            return result;
        }

        return MergeNames(TypeName(a), TypeName(b));
    }

    private static string TypeName(object descriptor) => descriptor switch
    {
        string s => s,
        Dictionary<string, object> d => (string)d["type"],
        _ => "unknown",
    };

    private static string MergeNames(string a, string b) =>
        string.Join('|', a.Split('|').Concat(b.Split('|')).Distinct().Order(StringComparer.Ordinal));
}
