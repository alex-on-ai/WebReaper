using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebReaper.Mcp;

namespace WebReaper.Mcp.AspNetCore;

// ADR-0086: the Streamable HTTP MCP host. Sibling to the stdio Program in
// WebReaper.Mcp; both wire the same WebReaperTools class. Factored as a
// builder method so the host smoke test can start the same app on a random
// loopback port without spawning a subprocess.
//
// Slice 1 (the spine): transport + tools + MapMcp + /health, loopback only,
// no auth. Auth, config validation, and the bind guard land in slice 2.
public static class McpHttpServer
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddMcpServer()
            // Stateless: WebReaper's tools are single-shot and make no
            // server-to-client requests, so the SDK's scalable mode fits.
            .WithHttpTransport(o => o.Stateless = true)
            // Reuse the stdio satellite's tools. WebReaperTools is a static
            // class (cannot be a generic type arg), so scan its assembly, the
            // same way the stdio host does with WithToolsFromAssembly.
            .WithToolsFromAssembly(typeof(WebReaperTools).Assembly);

        var app = builder.Build();

        // Streamable HTTP MCP endpoint at the application root.
        app.MapMcp();

        // Liveness probe for container orchestration.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        return app;
    }

    // Test seam: start the app on a random loopback port and report the bound
    // base address, so the host smoke test can drive a real HTTP MCP handshake
    // without a subprocess or a hard-coded port. Internal, visible to the test
    // projects only.
    internal static async Task<(WebApplication App, Uri BaseAddress)> StartForTestAsync(string[] args)
    {
        var app = Build(args);
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return (app, new Uri(address));
    }
}
