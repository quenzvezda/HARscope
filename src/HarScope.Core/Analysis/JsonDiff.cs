using System.Text.Json;
using System.Text.RegularExpressions;

namespace HarScope.Core.Analysis;

public sealed record DiffItem(string Path, string Change, object? A = null, object? B = null);

/// <summary>Structural JSON diff: objects by key, arrays by index. Values in the output are kept short.</summary>
public sealed class JsonDiff
{
    private const int MaxValueChars = 200;
    private readonly IReadOnlyList<Regex> _ignore;
    private readonly int _maxItems;

    public List<DiffItem> Items { get; } = [];
    public int Total { get; private set; }
    public bool Truncated => Total > Items.Count;

    /// <param name="ignorePaths">Paths to skip, e.g. "$.meta.timestamp" or "$.data[*].updatedAt"; children are skipped too.</param>
    public JsonDiff(IEnumerable<string>? ignorePaths = null, int maxItems = 50)
    {
        _maxItems = maxItems;
        _ignore = (ignorePaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(ToRegex).ToList();
    }

    public static JsonDiff Compare(JsonElement a, JsonElement b, IEnumerable<string>? ignorePaths = null, int maxItems = 50)
    {
        var diff = new JsonDiff(ignorePaths, maxItems);
        diff.Walk("$", a, b);
        return diff;
    }

    private void Walk(string path, JsonElement a, JsonElement b)
    {
        if (IsIgnored(path)) return;

        if (a.ValueKind != b.ValueKind && !(IsBool(a) && IsBool(b)))
        {
            Add(path, "typeChanged", Short(a), Short(b));
            return;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var p in a.EnumerateObject())
                {
                    var child = Child(path, p.Name);
                    if (bProps.Remove(p.Name, out var bv)) Walk(child, p.Value, bv);
                    else if (!IsIgnored(child)) Add(child, "removed", Short(p.Value));
                }
                foreach (var (name, value) in bProps)
                {
                    var child = Child(path, name);
                    if (!IsIgnored(child)) Add(child, "added", null, Short(value));
                }
                break;

            case JsonValueKind.Array:
                var la = a.GetArrayLength();
                var lb = b.GetArrayLength();
                if (la != lb) Add($"{path}.length", "changed", la, lb);
                using (var ea = a.EnumerateArray())
                using (var eb = b.EnumerateArray())
                {
                    var i = 0;
                    var hasA = ea.MoveNext();
                    var hasB = eb.MoveNext();
                    while (hasA || hasB)
                    {
                        var child = $"{path}[{i}]";
                        if (hasA && hasB) Walk(child, ea.Current, eb.Current);
                        else if (!IsIgnored(child))
                        {
                            if (hasA) Add(child, "removed", Short(ea.Current));
                            else Add(child, "added", null, Short(eb.Current));
                        }
                        if (hasA) hasA = ea.MoveNext();
                        if (hasB) hasB = eb.MoveNext();
                        i++;
                    }
                }
                break;

            case JsonValueKind.Number:
                var equal = a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetRawText() == b.GetRawText();
                if (!equal) Add(path, "changed", Short(a), Short(b));
                break;

            default:
                if (a.GetRawText() != b.GetRawText()) Add(path, "changed", Short(a), Short(b));
                break;
        }
    }

    private void Add(string path, string change, object? a = null, object? b = null)
    {
        Total++;
        if (Items.Count < _maxItems) Items.Add(new DiffItem(path, change, a, b));
    }

    private bool IsIgnored(string path) => _ignore.Any(r => r.IsMatch(path));

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Child(string path, string name) =>
        name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? $"{path}.{name}"
            : $"{path}['{name}']";

    /// <summary>Scalars as-is (strings shortened); objects/arrays as a size summary unless tiny.</summary>
    public static object? Short(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString()!;
                return s.Length <= MaxValueChars ? s : s[..MaxValueChars] + $"…[+{s.Length - MaxValueChars}]";
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object or JsonValueKind.Array:
                var raw = e.GetRawText();
                if (raw.Length <= MaxValueChars) return e.Clone();
                return e.ValueKind == JsonValueKind.Array ? $"array({e.GetArrayLength()})" : $"object({e.EnumerateObject().Count()} keys)";
            default:
                return e.Clone();
        }
    }

    /// <summary>"$.a[*].b" matches "$.a[3].b" and anything below it.</summary>
    private static Regex ToRegex(string pattern)
    {
        var p = pattern.Trim();
        if (!p.StartsWith('$')) p = p.StartsWith('[') ? "$" + p : "$." + p;
        var escaped = Regex.Escape(p).Replace(@"\[\*]", @"\[\d+]").Replace(@"\.\*", @"(\.[^.\[]+|\['[^']*'])");
        return new Regex("^" + escaped + @"($|[.\[])", RegexOptions.CultureInvariant);
    }
}
