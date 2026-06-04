using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebReaper.Mcp;

namespace WebReaper.Mcp.AspNetCore;

// ADR-0086: the Streamable HTTP MCP host. Sibling to the stdio Program in
// WebReaper.Mcp; both wire the same WebReaperTools class. Factored as builder
// methods so the host smoke test can start the same app on a random loopback
// port without spawning a subprocess.
public static class McpHttpServer
{
    public static WebApplication Build(string[] args)
    {
        var builder = CreateBuilder(args);
        // Token from env; bind URLs from ASP.NET configuration (--urls /
        // ASPNETCORE_URLS aggregate into config["urls"]). Validate throws an
        // actionable error on an unsafe non-loopback-without-token bind.
        var options = McpHttpOptions.Validate(
            Environment.GetEnvironmentVariable(McpHttpOptions.TokenEnvVar),
            ResolveBindUrls(builder.Configuration));
        return Assemble(builder, options);
    }

    private static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services
            .AddMcpServer()
            // Stateless: WebReaper's tools are single-shot and make no
            // server-to-client requests, so the SDK's scalable mode fits.
            .WithHttpTransport(o => o.Stateless = true)
            // Reuse the stdio satellite's tools (WebReaperTools is a static
            // class, so scan its assembly rather than name the type).
            .WithToolsFromAssembly(typeof(WebReaperTools).Assembly);
        return builder;
    }

    private static WebApplication Assemble(WebApplicationBuilder builder, McpHttpOptions options)
    {
        var app = builder.Build();

        // Bearer-token gate (only when a token is configured). /health stays
        // open so orchestrators can probe without the token.
        if (options.RequireAuth)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                var header = context.Request.Headers.Authorization.ToString();
                if (!BearerAuth.IsAuthorized(options.BearerToken, header))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Unauthorized: missing or invalid bearer token.");
                    return;
                }

                await next();
            });
        }

        // Streamable HTTP MCP endpoint at the application root.
        app.MapMcp();

        // Liveness probe for container orchestration.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        return app;
    }

    // ASP.NET aggregates --urls / ASPNETCORE_URLS / DOTNET_URLS into
    // config["urls"]. Absent => Kestrel's loopback default.
    private static IEnumerable<string> ResolveBindUrls(IConfiguration config)
    {
        var urls = config["urls"];
        return string.IsNullOrWhiteSpace(urls)
            ? ["http://localhost:5000"]
            : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Test seam: start the app on a random loopback port and report the bound
    // base address, so the host smoke test can drive a real HTTP MCP handshake
    // without a subprocess or a hard-coded port. An optional token exercises
    // the auth path without mutating process-global environment state.
    internal static async Task<(WebApplication App, Uri BaseAddress)> StartForTestAsync(
        string[] args, string? token = null)
    {
        var builder = CreateBuilder(args);
        // Loopback bind, so a token is allowed but not required by the guard.
        var options = McpHttpOptions.Validate(token, ["http://127.0.0.1:0"]);
        var app = Assemble(builder, options);

        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return (app, new Uri(address));
    }
}
