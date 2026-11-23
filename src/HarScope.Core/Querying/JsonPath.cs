using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HarScope.Core.Querying;

/// <summary>
/// A deliberately small JSONPath subset. Supported:
/// <list type="bullet">
///   <item><c>$</c> root (optional: relative paths like <c>data.items</c> are accepted)</item>
///   <item><c>.name</c> and <c>['name']</c> / <c>["name"]</c> property access</item>
///   <item><c>[n]</c> array index, negative counts from the end (<c>[-1]</c> = last)</item>
///   <item><c>[*]</c> / <c>.*</c> wildcard over array elements or object property values</item>
///   <item><c>[start:end]</c> array slice (end exclusive, either side optional, negatives allowed)</item>
/// </list>
/// Not supported: recursive descent (<c>..</c>), filters (<c>?()</c>), unions, script expressions.
/// </summary>
public sealed class JsonPath
{
    /// <summary>Upper bound on nodes produced by one evaluation, to keep wildcard fan-out bounded.</summary>
    public const int MaxMatches = 50_000;

    private abstract record Segment;
    private sealed record Property(string Name) : Segment;
    private sealed record Index(int Value) : Segment;
    private sealed record Wildcard : Segment;
    private sealed record Slice(int? Start, int? End) : Segment;

    private readonly IReadOnlyList<Segment> _segments;

    public string Text { get; }
    public bool HasWildcard => _segments.Any(s => s is Wildcard or Slice);

    private JsonPath(string text, IReadOnlyList<Segment> segments)
    {
        Text = text;
        _segments = segments;
    }

    public static JsonPath Parse(string? path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0 || p == "$") return new JsonPath("$", []);

        var segments = new List<Segment>();
        var i = 0;
        if (p[0] == '$') i = 1;
        else if (p[0] != '[' && p[0] != '.') p = "." + p; // relative path: "data.items" == "$.data.items"

        while (i < p.Length)
        {
            var c = p[i];
            if (c == '.')
            {
                if (i + 1 < p.Length && p[i + 1] == '.')
                    throw Error(path, "recursive descent '..' is not supported");
                i++;
                if (i < p.Length && p[i] == '*')
                {
                    segments.Add(new Wildcard());
                    i++;
                    continue;
                }
                var start = i;
                while (i < p.Length && p[i] != '.' && p[i] != '[') i++;
                if (i == start) throw Error(path, $"expected a property name at position {start}");
                segments.Add(new Property(p[start..i]));
            }
            else if (c == '[')
            {
                var close = FindClose(p, i, path);
                var inner = p[(i + 1)..close].Trim();
                segments.Add(ParseBracket(inner, path));
                i = close + 1;
            }
            else
            {
                throw Error(path, $"unexpected '{c}' at position {i}");
            }
        }

        return new JsonPath(Canonical(segments), segments);
    }

    /// <summary>Evaluates the path; each match carries its concrete path (wildcards resolved).</summary>
    public List<(string Path, JsonElement Value)> Evaluate(JsonElement root, out bool capped)
    {
        var current = new List<(string Path, JsonElement Value)> { ("$", root) };
        capped = false;

        foreach (var segment in _segments)
        {
            var next = new List<(string, JsonElement)>();
            foreach (var (path, value) in current)
            {
                foreach (var item in Step(path, value, segment))
                {
                    if (next.Count >= MaxMatches)
                    {
                        capped = true;
                        break;
                    }
                    next.Add(item);
                }
                if (capped) break;
            }
            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }

    private static IEnumerable<(string, JsonElement)> Step(string path, JsonElement value, Segment segment)
    {
        switch (segment)
        {
            case Property(var name):
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var prop))
                    yield return (AppendName(path, name), prop);
                break;

            case Index(var index):
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var length = value.GetArrayLength();
                    var actual = index < 0 ? length + index : index;
                    if (actual >= 0 && actual < length) yield return ($"{path}[{actual}]", value[actual]);
                }
                break;

            case Wildcard:
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var n = 0;
                    foreach (var item in value.EnumerateArray()) yield return ($"{path}[{n++}]", item);
                }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in value.EnumerateObject()) yield return (AppendName(path, p.Name), p.Value);
                }
                break;

            case Slice(var start, var end):
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var length = value.GetArrayLength();
                    var from = Normalize(start ?? 0, length);
                    var to = Normalize(end ?? length, length);
                    // Enumerate instead of indexing: element lookup by index is not O(1) for nested arrays.
                    var k = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (k >= to) break;
                        if (k >= from) yield return ($"{path}[{k}]", item);
                        k++;
                    }
                }
                break;
        }
    }

    private static int Normalize(int index, int length) => Math.Clamp(index < 0 ? length + index : index, 0, length);

    private static Segment ParseBracket(string inner, string? original)
    {
        if (inner == "*") return new Wildcard();
        if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
            return new Property(inner[1..^1]);
        if (inner.Contains(':'))
        {
            var parts = inner.Split(':');
            if (parts.Length != 2) throw Error(original, "slice steps are not supported; use [start:end]");
            return new Slice(OptionalInt(parts[0], original), OptionalInt(parts[1], original));
        }
        if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            return new Index(index);
        if (inner.StartsWith('?')) throw Error(original, "filter expressions [?()] are not supported");
        throw Error(original, $"unsupported bracket expression [{inner}]");
    }

    private static int? OptionalInt(string s, string? original)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw Error(original, $"'{s}' is not an integer");
    }

    private static int FindClose(string p, int open, string? original)
    {
        var quote = '\0';
        for (var k = open + 1; k < p.Length; k++)
        {
            var c = p[k];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == ']') return k;
        }
        throw Error(original, "unclosed '['");
    }

    private static string AppendName(string path, string name) =>
        IsPlainName(name) ? $"{path}.{name}" : $"{path}['{name.Replace("'", "\\'")}']";

    private static bool IsPlainName(string name) =>
        name.Length > 0 && !char.IsDigit(name[0]) && name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$');

    private static string Canonical(IEnumerable<Segment> segments)
    {
        var sb = new StringBuilder("$");
        foreach (var s in segments)
        {
            sb.Append(s switch
            {
                Property(var n) => IsPlainName(n) ? $".{n}" : $"['{n}']",
                Index(var i) => $"[{i}]",
                Wildcard => "[*]",
                Slice(var a, var b) => $"[{a}:{b}]",
                _ => "",
            });
        }
        return sb.ToString();
    }

    private static HarScopeException Error(string? path, string detail) =>
        new(ErrorCodes.InvalidArgument,
            $"Invalid path '{path}': {detail}. Supported: $, .name, ['name'], [n], [-n], [*], .*, [start:end].");
}
