using HarScope.Core;
using HarScope.Core.Indexing;
using HarScope.Core.Querying;
using HarScope.Core.Security;
using HarScope.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HarScope.Mcp.Tests;

/// <summary>Temporary HAR_ROOT with the sample capture, plus a HarTools instance wired like Program.cs.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "harscope-mcp-tests", Guid.NewGuid().ToString("N"));
    public HarScopeOptions Options { get; }

    public Fixture(Action<HarScopeOptions>? configure = null)
    {
        Directory.CreateDirectory(Root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "checkout-flow.har"), Path.Combine(Root, "checkout-flow.har"));
        Options = new HarScopeOptions { RootPath = Root };
        configure?.Invoke(Options);
    }

    public HarTools CreateTools()
    {
        var redactor = new Redactor(Options.Redaction);
        return new HarTools(
            new CaptureStore(Options),
            new OverviewService(redactor),
            new SearchService(Options, redactor),
            new EntryInspector(Options, redactor),
            new BodyQueryService(Options),
            new OutputGuard(Options),
            NullLogger<HarTools>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
