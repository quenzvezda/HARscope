using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarScope.Core.Querying;

/// <summary>Serializer settings for tool results: compact, camelCase, nulls omitted.</summary>
public static class HarJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Results are embedded as text in an MCP message; avoid \uXXXX inflation of non-ASCII and <>&.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
