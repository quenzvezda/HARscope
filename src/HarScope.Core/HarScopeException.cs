namespace HarScope.Core;

/// <summary>
/// An expected, caller-facing failure (bad path, unknown entry, invalid HAR...).
/// The message is safe to show to the agent: it never contains HAR content or credentials.
/// </summary>
public sealed class HarScopeException(string code, string message) : Exception(message)
{
    /// <summary>Stable machine-readable error code, e.g. "path_not_allowed".</summary>
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string NotConfigured = "not_configured";
    public const string PathNotAllowed = "path_not_allowed";
    public const string FileNotFound = "file_not_found";
    public const string FileTooLarge = "file_too_large";
    public const string InvalidHar = "invalid_har";
    public const string UnknownCapture = "unknown_capture";
    public const string NoActiveCapture = "no_active_capture";
    public const string UnknownEntry = "unknown_entry";
    public const string InvalidArgument = "invalid_argument";
}
