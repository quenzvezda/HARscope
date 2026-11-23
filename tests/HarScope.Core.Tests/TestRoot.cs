using HarScope.Core;
using HarScope.Core.Indexing;
using HarScope.Core.Querying;
using HarScope.Core.Security;

namespace HarScope.Core.Tests;

/// <summary>A temporary HAR_ROOT populated with the synthetic sample capture.</summary>
public sealed class TestRoot : IDisposable
{
    public string Root { get; }
    public HarScopeOptions Options { get; }
    public Redactor Redactor { get; }
    public CaptureStore Store { get; }

    public TestRoot(Action<HarScopeOptions>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "harscope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        File.Copy(SamplePath, Path.Combine(Root, "checkout-flow.har"));

        Options = new HarScopeOptions { RootPath = Root };
        configure?.Invoke(Options);
        Redactor = new Redactor(Options.Redaction);
        Store = new CaptureStore(Options);
    }

    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "samples", "checkout-flow.har");

    public SearchService Search => new(Options, Redactor);
    public EntryInspector Inspector => new(Options, Redactor);
    public OverviewService Overview => new(Redactor);

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public CaptureHandle OpenSample() => Store.Open("checkout-flow.har");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class HarText
{
    /// <summary>Builds a minimal HAR document from entry JSON fragments.</summary>
    public static string Of(params string[] entries) =>
        $$$"""{"log":{"version":"1.2","creator":{"name":"test","version":"1"},"entries":[{{{string.Join(",", entries)}}}]}}""";

    public static string Entry(string url, string method = "GET", int status = 200, string started = "2026-01-01T00:00:00Z",
        double time = 10, string content = """{"size":0,"mimeType":""}""") =>
        $$$"""
        {"startedDateTime":"{{{started}}}","time":{{{time}}},
         "request":{"method":"{{{method}}}","url":"{{{url}}}","httpVersion":"h2","headers":[],"queryString":[],"cookies":[],"headersSize":-1,"bodySize":0},
         "response":{"status":{{{status}}},"statusText":"","httpVersion":"h2","headers":[],"cookies":[],"content":{{{content}}},"redirectURL":"","headersSize":-1,"bodySize":-1},
         "cache":{},"timings":{"send":0,"wait":10,"receive":0}}
        """;
}
