using System.Globalization;
using System.Text.Json;

namespace HarScope.Core.Querying;

/// <summary>
/// One row filter for har_query_body, written as "&lt;field&gt; &lt;op&gt; [value]", e.g.
/// <c>status == 'FAILED'</c>, <c>jumlah &gt;= 10</c>, <c>namaDebitur notContains SYAFRIANDI</c>, <c>errorCode exists</c>.
/// The field is a path relative to each match ("@" means the match itself).
/// Missing fields never satisfy ==, &lt;, &gt;, contains, startsWith or exists; they do satisfy != and notContains.
/// </summary>
public sealed class WhereClause
{
    private enum Op { Eq, Ne, Gt, Ge, Lt, Le, Contains, NotContains, StartsWith, Exists, NotExists }

    private static readonly Dictionary<string, Op> Ops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["=="] = Op.Eq, ["="] = Op.Eq, ["!="] = Op.Ne, ["<>"] = Op.Ne,
        [">"] = Op.Gt, [">="] = Op.Ge, ["<"] = Op.Lt, ["<="] = Op.Le,
        ["contains"] = Op.Contains, ["notContains"] = Op.NotContains, ["startsWith"] = Op.StartsWith,
        ["exists"] = Op.Exists, ["notExists"] = Op.NotExists,
    };

    private readonly JsonPath? _field;
    private readonly Op _op;
    private readonly JsonElement? _value;
    private readonly string? _text;

    public string Text { get; }

    private WhereClause(string text, JsonPath? field, Op op, JsonElement? value)
    {
        Text = text;
        _field = field;
        _op = op;
        _value = value;
        _text = value is { ValueKind: JsonValueKind.String } s ? s.GetString() : value?.GetRawText();
    }

    public static WhereClause Parse(string expression)
    {
        var text = (expression ?? "").Trim();
        var firstSpace = text.IndexOfAny([' ', '\t']);
        if (firstSpace <= 0) throw Error(expression, "expected '<field> <op> [value]'");

        var fieldText = text[..firstSpace];
        var rest = text[firstSpace..].TrimStart();
        var opEnd = rest.IndexOfAny([' ', '\t']);
        var opText = opEnd < 0 ? rest : rest[..opEnd];
        var valueText = opEnd < 0 ? "" : rest[opEnd..].Trim();

        if (!Ops.TryGetValue(opText, out var op))
            throw Error(expression, $"unknown operator '{opText}'");

        var field = fieldText == "@" ? null : JsonPath.Parse(fieldText);

        if (op is Op.Exists or Op.NotExists)
        {
            if (valueText.Length > 0) throw Error(expression, $"'{opText}' takes no value");
            return new WhereClause(text, field, op, null);
        }
        if (valueText.Length == 0) throw Error(expression, $"'{opText}' needs a value");

        return new WhereClause(text, field, op, ParseValue(valueText));
    }

    /// <summary>Numbers, true/false/null, 'quoted' or "quoted" strings; anything else is a bare string.</summary>
    private static JsonElement ParseValue(string raw)
    {
        if (raw.Length >= 2 && (raw[0] == '\'' || raw[0] == '"') && raw[^1] == raw[0])
            return JsonSerializer.SerializeToElement(raw[1..^1]);
        if (raw is "true" or "false" or "null")
            return JsonDocument.Parse(raw).RootElement.Clone();
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonSerializer.SerializeToElement(d);
        return JsonSerializer.SerializeToElement(raw);
    }

    public bool Matches(JsonElement item)
    {
        JsonElement? field;
        if (_field is null) field = item;
        else
        {
            var found = _field.Evaluate(item, out _);
            field = found.Count == 0 ? null : found[0].Value;
        }

        var present = field is { } f && f.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        return _op switch
        {
            Op.Exists => present,
            Op.NotExists => !present,
            Op.Eq => field is { } a && Equal(a, _value!.Value),
            Op.Ne => field is not { } b || !Equal(b, _value!.Value),
            Op.Contains => field is { } c && ContainsText(c),
            Op.NotContains => field is not { } nc || !ContainsText(nc),
            Op.StartsWith => field is { ValueKind: JsonValueKind.String } s &&
                             s.GetString()!.StartsWith(_text!, StringComparison.OrdinalIgnoreCase),
            _ => field is { } g && Compare(g, _value!.Value) is { } cmp && _op switch
            {
                Op.Gt => cmp > 0,
                Op.Ge => cmp >= 0,
                Op.Lt => cmp < 0,
                _ => cmp <= 0,
            },
        };
    }

    private static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetRawText() == b.GetRawText();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return a.GetString() == b.GetString();
        return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
    }

    private static int? Compare(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number &&
            a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y))
            return x.CompareTo(y);
        // Strings compare ordinally, which also orders ISO-8601 timestamps correctly.
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return string.CompareOrdinal(a.GetString(), b.GetString());
        return null;
    }

    /// <summary>Case-insensitive substring; for arrays, true when any element contains the text.</summary>
    private bool ContainsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(_text!, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsText),
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => value.GetRawText().Contains(_text!, StringComparison.OrdinalIgnoreCase),
    };

    private static HarScopeException Error(string? expression, string detail) =>
        new(ErrorCodes.InvalidArgument,
            $"Invalid where '{expression}': {detail}. Form: '<field> <op> [value]' with op one of " +
            "==, !=, >, >=, <, <=, contains, notContains, startsWith, exists, notExists. " +
            "Example: \"status == 'FAILED'\", \"amount > 100\", \"name notContains SARI\".");
}
