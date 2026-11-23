using HarScope.Core;
using HarScope.Core.Indexing;
using HarScope.Core.Querying;
using HarScope.Core.Security;
using HarScope.Mcp.Configuration;
using HarScope.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Look for an optional appsettings.json next to the executable, not in the client's working directory.
    ContentRootPath = AppContext.BaseDirectory,
});

// stdout carries the MCP protocol: every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

var options = OptionsLoader.Load(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new Redactor(options.Redaction));
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddSingleton<OverviewService>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<EntryInspector>();
builder.Services.AddSingleton<OutputGuard>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "harscope", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<HarTools>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HarScope");
if (options.RootPath is null)
    logger.LogWarning("HAR_ROOT is not set; every tool will report 'not_configured' until it is.");
else
    logger.LogInformation("HAR_ROOT = {Root}; redaction {Redaction}", options.RootPath,
        options.Redaction.Enabled ? "on" : "off");

await app.RunAsync();
