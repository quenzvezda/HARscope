using System.Reflection;
using System.Text.Json;
using HarScope.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HarScope.Mcp.Tests;

public class HarToolsTests
{
    [Fact]
    public void Exposes_all_tools_as_read_only()
    {
        var tools = typeof(HarTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .ToList();

        Assert.Equal(["har_compare_captures", "har_compare_entries", "har_get_entry", "har_list", "har_open", "har_overview", "har_query_body", "har_repro", "har_search", "har_trace_flow"],
            tools.Select(t => t!.Name!).Order().ToArray());
        Assert.All(tools, t => Assert.True(t!.ReadOnly));
    }

    [Fact]
    public void Open_search_and_get_entry_round_trip()
    {
        using var f = new Fixture();
        var tools = f.CreateTools();

        using var open = JsonDocument.Parse(tools.HarOpen("checkout-flow.har"));
        Assert.Equal("checkout-flow", open.RootElement.GetProperty("captureId").GetString());
        Assert.Equal(13, open.RootElement.GetProperty("entries").GetInt32());

        using var search = JsonDocument.Parse(tools.HarSearch(failedOnly: true));
        var ids = search.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["checkout-flow:entry-8", "checkout-flow:entry-9"], ids);

        using var entry = JsonDocument.Parse(tools.HarGetEntry(ids[0]!, ["responseBody"]));
        Assert.Contains("GATEWAY_TIMEOUT", entry.RootElement.GetProperty("responseBody").GetProperty("text").GetString());
    }

    [Fact]
    public void Output_uses_camel_case_and_omits_nulls()
    {
        using var f = new Fixture();
        var tools = f.CreateTools();
        tools.HarOpen("checkout-flow.har");

        var json = tools.HarGetEntry("checkout-flow:entry-1");
        Assert.Contains("\"summary\":", json);
        Assert.DoesNotContain("\"responseBody\":", json);
        Assert.DoesNotContain(":null", json);
    }

    [Fact]
    public void Overview_respects_output_limit()
    {
        using var f = new Fixture(o => o.MaxOutputChars = 2_000);
        var tools = f.CreateTools();
        tools.HarOpen("checkout-flow.har");

        var json = tools.HarOverview();
        Assert.True(json.Length <= 2_000);
    }

    [Theory]
    [InlineData("../outside.har", "path_not_allowed")]
    [InlineData("missing.har", "file_not_found")]
    public void Expected_failures_become_mcp_errors_with_codes(string path, string code)
    {
        using var f = new Fixture();
        var ex = Assert.Throws<McpException>(() => f.CreateTools().HarOpen(path));
        Assert.StartsWith(code + ":", ex.Message);
    }

    [Fact]
    public void Invalid_search_arguments_are_reported()
    {
        using var f = new Fixture();
        var tools = f.CreateTools();
        tools.HarOpen("checkout-flow.har");

        Assert.StartsWith("invalid_argument:", Assert.Throws<McpException>(() => tools.HarSearch(sort: "size")).Message);
        Assert.StartsWith("invalid_argument:", Assert.Throws<McpException>(() => tools.HarSearch(from: "yesterday")).Message);
    }

    [Fact]
    public void Tools_require_an_open_capture()
    {
        using var f = new Fixture();
        var ex = Assert.Throws<McpException>(() => f.CreateTools().HarOverview());
        Assert.StartsWith("no_active_capture:", ex.Message);
    }
}
