using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WebReaper.IntegrationTests.Fixtures;
using WebReaper.Mcp.AspNetCore;
using WebReaper.TestServer;
using Xunit;

namespace WebReaper.IntegrationTests;

/// <summary>
/// ADR-0086: end-to-end coverage of the Streamable HTTP MCP host
/// (WebReaper.Mcp.AspNetCore). The real host is started on a random loopback
/// port and driven with the official MCP SDK client over Streamable HTTP, a
/// true initialize -> tools/list -> tools/call handshake. The HTTP analog of
/// McpServerTests (which does the same over stdio). Tools run against the
/// deterministic local site. Tagged Mcp so the fast gate skips it (real HTTP
/// stack + protocol startup per test).
/// </summary>
[Collection("LocalSite")]
[Trait("Category", "Mcp")]
public sealed class McpHttpServerTests
{
    private readonly LocalTestSite _site;

    public McpHttpServerTests(LocalSiteFixture fixture) => _site = fixture.Site;

    private static async Task<(McpClient Client, IAsyncDisposable App)> StartAsync()
    {
        var (app, baseAddress) = await McpHttpServer.StartForTestAsync([]);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = baseAddress,
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        var client = await McpClient.CreateAsync(transport);
        return (client, app);
    }

    private static string FirstText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().First().Text;

    [Fact]
    public async Task Server_lists_the_tools_over_http()
    {
        var (client, app) = await StartAsync();
        await using (app)
        await using (client)
        {
            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
            Assert.Contains("scrape", names);
            Assert.Contains("map", names);
            Assert.Contains("extract", names);
        }
    }

    [Fact]
    public async Task Scrape_tool_returns_markdown_over_http()
    {
        var (client, app) = await StartAsync();
        await using (app)
        await using (client)
        {
            var result = await client.CallToolAsync(
                "scrape",
                new Dictionary<string, object?> { ["url"] = _site.Url("/static") },
                cancellationToken: CancellationToken.None);

            Assert.Contains("Widget Pro 3000", FirstText(result));
        }
    }

    [Fact]
    public async Task Health_endpoint_responds()
    {
        var (app, baseAddress) = await McpHttpServer.StartForTestAsync([]);
        await using (app)
        {
            using var http = new HttpClient();
            var response = await http.GetAsync(new Uri(baseAddress, "/health"));
            Assert.True(response.IsSuccessStatusCode);
        }
    }
}
