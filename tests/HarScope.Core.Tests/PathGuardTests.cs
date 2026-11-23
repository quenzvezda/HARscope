namespace HarScope.Core.Tests;

public class PathGuardTests
{
    [Theory]
    [InlineData("../secret.har")]
    [InlineData("team/../../secret.har")]
    [InlineData("..\\secret.har")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("checkout-flow.har:stream")]
    [InlineData("checkout-flow.json")]
    public void Rejects_paths_outside_root_or_non_har(string path)
    {
        using var t = new TestRoot();
        var ex = Assert.Throws<HarScopeException>(() => t.Store.Open(path));
        Assert.Equal(ErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void Reports_missing_file_inside_root()
    {
        using var t = new TestRoot();
        var ex = Assert.Throws<HarScopeException>(() => t.Store.Open("missing.har"));
        Assert.Equal(ErrorCodes.FileNotFound, ex.Code);
    }

    [Fact]
    public void Rejects_symlink_pointing_outside_root()
    {
        using var t = new TestRoot();
        var outsideDir = Path.Combine(Path.GetTempPath(), "harscope-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            File.WriteAllText(Path.Combine(outsideDir, "secret.har"), HarText.Of());
            // A directory link inside the root that points outside it.
            Assert.True(CreateDirectoryLink(Path.Combine(t.Root, "linked"), outsideDir),
                "Could not create a symlink or junction for this test.");

            var ex = Assert.Throws<HarScopeException>(() => t.Store.Open("linked/secret.har"));
            Assert.Equal(ErrorCodes.PathNotAllowed, ex.Code);

            // Links are not followed when listing either.
            Assert.DoesNotContain(t.Store.List(out _), f => f.Path.Contains("secret"));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    /// <summary>Symlink when permitted; otherwise a Windows junction, which needs no special rights.</summary>
    private static bool CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }

        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Missing_root_is_reported_as_not_configured()
    {
        var store = new Indexing.CaptureStore(new HarScopeOptions { RootPath = null });
        var ex = Assert.Throws<HarScopeException>(() => store.Open("x.har"));
        Assert.Equal(ErrorCodes.NotConfigured, ex.Code);
    }
}
