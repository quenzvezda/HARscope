using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HarScope.Mcp.Tests;

/// <summary>
/// Starts the real server process and talks to it over stdio with the official MCP client,
/// the same way Claude Code or another MCP client would.
/// </summary>
public class StdioEndToEndTests
{
    private static async Task<McpClient> ConnectAsync(string root)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "HarScope.Mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "harscope",
            Command = "dotnet",
            Arguments = [serverDll],
            EnvironmentVariables = new Dictionary<string, string?> { ["HAR_ROOT"] = root },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Client_discovers_and_invokes_tools_over_stdio()
    {
        using var f = new Fixture();
        await using var client = await ConnectAsync(f.Root);

        var tools = await client.ListToolsAsync();
        Assert.Equal(["har_get_entry", "har_list", "har_open", "har_overview", "har_search"],
            tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        var list = await client.CallToolAsync("har_list", new Dictionary<string, object?>());
        Assert.Contains("checkout-flow.har", Text(list));

        var open = await client.CallToolAsync("har_open", new Dictionary<string, object?> { ["path"] = "checkout-flow.har" });
        Assert.NotEqual(true, open.IsError);

        var search = await client.CallToolAsync("har_search", new Dictionary<string, object?>
        {
            ["statusMin"] = 500,
            ["limit"] = 5,
        });
        using var searchDoc = JsonDocument.Parse(Text(search));
        Assert.Equal(1, searchDoc.RootElement.GetProperty("totalMatches").GetInt32());

        var entry = await client.CallToolAsync("har_get_entry", new Dictionary<string, object?>
        {
            ["entryId"] = "checkout-flow:entry-3",
            ["sections"] = new[] { "requestHeaders" },
        });
        var entryText = Text(entry);
        Assert.Contains("[REDACTED", entryText);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.", entryText);
    }

    [Fact]
    public async Task Errors_are_returned_as_tool_errors()
    {
        using var f = new Fixture();
        await using var client = await ConnectAsync(f.Root);

        var result = await client.CallToolAsync("har_open", new Dictionary<string, object?> { ["path"] = "../../secret.har" });

        Assert.True(result.IsError);
        Assert.Contains("path_not_allowed", Text(result));
    }
}
