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
        Assert.Equal(["har_compare_captures", "har_compare_entries", "har_get_entry", "har_list", "har_open", "har_overview", "har_query_body", "har_repro", "har_search", "har_trace_flow"],
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

        var byBody = await client.CallToolAsync("har_search", new Dictionary<string, object?>
        {
            ["bodyContains"] = "GATEWAY_TIMEOUT",
        });
        Assert.Contains("checkout-flow:entry-8", Text(byBody));

        var projected = await client.CallToolAsync("har_query_body", new Dictionary<string, object?>
        {
            ["entryId"] = "checkout-flow:entry-4",
            ["path"] = "$.items[*]",
            ["fields"] = new[] { "sku", "price" },
        });
        Assert.NotEqual(true, projected.IsError);
        Assert.Contains("{\"sku\":\"B2\",\"price\":79.0}", Text(projected));

        var sum = await client.CallToolAsync("har_query_body", new Dictionary<string, object?>
        {
            ["entryId"] = "checkout-flow:entry-4",
            ["path"] = "$.items[*].price",
            ["aggregate"] = "sum",
        });
        using var sumDoc = JsonDocument.Parse(Text(sum));
        Assert.Equal(138.9m, sumDoc.RootElement.GetProperty("aggregate").GetProperty("value").GetDecimal());

        var filtered = await client.CallToolAsync("har_query_body", new Dictionary<string, object?>
        {
            ["entryId"] = "checkout-flow:entry-4",
            ["path"] = "$.items[*]",
            ["where"] = new[] { "price > 60" },
            ["fields"] = new[] { "sku" },
        });
        using var filteredDoc = JsonDocument.Parse(Text(filtered));
        Assert.Equal(1, filteredDoc.RootElement.GetProperty("matched").GetInt32());

        var flow = await client.CallToolAsync("har_trace_flow", new Dictionary<string, object?>());
        using var flowDoc = JsonDocument.Parse(Text(flow));
        Assert.Equal(7, flowDoc.RootElement.GetProperty("totalGroups").GetInt32());

        var diff = await client.CallToolAsync("har_compare_entries", new Dictionary<string, object?>
        {
            ["entryIdA"] = "checkout-flow:entry-7",
            ["entryIdB"] = "checkout-flow:entry-8",
        });
        Assert.Contains("status 200 -> 502", Text(diff));

        var repro = await client.CallToolAsync("har_repro", new Dictionary<string, object?>
        {
            ["entryIds"] = new[] { "checkout-flow:entry-2" },
        });
        var reproText = Text(repro);
        Assert.Contains("{{password}}", reproText);
        Assert.DoesNotContain("hunter2", reproText);
    }

    [Fact]
    public async Task Server_sends_usage_instructions()
    {
        using var f = new Fixture();
        await using var client = await ConnectAsync(f.Root);

        Assert.Contains("Do not read .har files directly", client.ServerInstructions);
        Assert.Equal("0.3.0", client.ServerInfo.Version);
    }

    [Fact]
    public async Task Compare_captures_over_stdio()
    {
        using var f = new Fixture();
        File.Copy(Path.Combine(f.Root, "checkout-flow.har"), Path.Combine(f.Root, "checkout-copy.har"));
        await using var client = await ConnectAsync(f.Root);

        await client.CallToolAsync("har_open", new Dictionary<string, object?> { ["path"] = "checkout-flow.har" });
        await client.CallToolAsync("har_open", new Dictionary<string, object?> { ["path"] = "checkout-copy.har" });
        var result = await client.CallToolAsync("har_compare_captures", new Dictionary<string, object?>
        {
            ["captureA"] = "checkout-flow",
            ["captureB"] = "checkout-copy",
        });

        using var doc = JsonDocument.Parse(Text(result));
        Assert.Equal(0, doc.RootElement.GetProperty("differentPairs").GetInt32());
        Assert.True(doc.RootElement.GetProperty("pairs").GetInt32() > 5);
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
