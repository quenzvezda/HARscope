namespace HarScope.Core.Security;

/// <summary>
/// Resolves caller-supplied relative paths to files inside the configured HAR root.
/// Rejects absolute paths, traversal ("..") and symlinks/junctions that point outside the root.
/// </summary>
public sealed class PathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Root { get; }

    public PathGuard(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new HarScopeException(ErrorCodes.NotConfigured, "HAR_ROOT is not configured.");

        var full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
            throw new HarScopeException(ErrorCodes.NotConfigured, $"HAR_ROOT directory does not exist: {full}");

        // If the root itself is a link, anchor on its final target.
        var info = new DirectoryInfo(full);
        if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            full = Path.GetFullPath(target.FullName);

        Root = Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>Returns the absolute path for an allowed relative path, or throws <see cref="HarScopeException"/>.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new HarScopeException(ErrorCodes.InvalidArgument, "Path is required.");

        var rel = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.Contains(':'))
            throw new HarScopeException(ErrorCodes.PathNotAllowed, "Only paths relative to HAR_ROOT are allowed.");
        if (rel.Split('/').Any(segment => segment == ".."))
            throw new HarScopeException(ErrorCodes.PathNotAllowed, "Path traversal ('..') is not allowed.");
        if (!rel.EndsWith(".har", StringComparison.OrdinalIgnoreCase))
            throw new HarScopeException(ErrorCodes.PathNotAllowed, "Only .har files can be opened.");

        var full = Path.GetFullPath(Path.Combine(Root, rel));
        EnsureInsideRoot(full);

        if (!File.Exists(full))
            throw new HarScopeException(ErrorCodes.FileNotFound, $"File not found under HAR_ROOT: {rel}");

        EnsureNoLinkEscape(full);
        return full;
    }

    /// <summary>Path relative to the root, always with forward slashes.</summary>
    public string ToRelative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public bool IsInsideRoot(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        return normalized.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);
    }

    private void EnsureInsideRoot(string fullPath)
    {
        if (!IsInsideRoot(fullPath))
            throw new HarScopeException(ErrorCodes.PathNotAllowed, "Path resolves outside HAR_ROOT.");
    }

    /// <summary>Walks every component below the root; any link must resolve back inside the root.</summary>
    private void EnsureNoLinkEscape(string fullPath)
    {
        var current = fullPath;
        while (!string.Equals(Path.TrimEndingDirectorySeparator(current), Root, PathComparison))
        {
            FileSystemInfo info = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !IsInsideRoot(target.FullName))
                    throw new HarScopeException(ErrorCodes.PathNotAllowed, "Path is a link that points outside HAR_ROOT.");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
    }
}
