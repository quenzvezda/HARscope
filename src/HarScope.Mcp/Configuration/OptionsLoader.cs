using HarScope.Core;
using Microsoft.Extensions.Configuration;

namespace HarScope.Mcp.Configuration;

/// <summary>
/// Reads settings from environment variables / command line / appsettings.json (in that precedence, highest first).
/// </summary>
public static class OptionsLoader
{
    public static HarScopeOptions Load(IConfiguration config)
    {
        var options = new HarScopeOptions { RootPath = Get(config, "HAR_ROOT") };

        if (Int(config, "HARSCOPE_MAX_FILE_MB") is { } mb) options.MaxFileBytes = mb * 1024L * 1024L;
        if (Int(config, "HARSCOPE_MAX_OUTPUT_CHARS") is { } chars) options.MaxOutputChars = Math.Max(2_000, chars);
        if (Int(config, "HARSCOPE_MAX_OPEN_CAPTURES") is { } open) options.MaxOpenCaptures = Math.Max(1, open);
        if (Int(config, "HARSCOPE_DEFAULT_BODY_CHARS") is { } body) options.DefaultBodyChars = Math.Max(100, body);

        if (Get(config, "HARSCOPE_REDACT") is { } redact)
            options.Redaction.Enabled = !(redact.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                          redact.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                                          redact == "0");

        foreach (var h in List(config, "HARSCOPE_REDACT_HEADERS")) options.Redaction.HeaderNames.Add(h);
        foreach (var q in List(config, "HARSCOPE_REDACT_QUERY")) options.Redaction.QueryParamNames.Add(q);

        return options;
    }

    private static string? Get(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } v ? v.Trim() : null;

    private static int? Int(IConfiguration config, string key) =>
        int.TryParse(Get(config, key), out var v) && v > 0 ? v : null;

    private static IEnumerable<string> List(IConfiguration config, string key) =>
        (Get(config, key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
