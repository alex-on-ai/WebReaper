// ADR-0086: WebReaper.Mcp.AspNetCore entry point. The Streamable HTTP MCP
// server a remote client (n8n, hosted agent) points a URL at. Tools live in
// WebReaper.Mcp's WebReaperTools; the host wiring is in McpHttpServer.
//
// Slice 1: loopback-only, no auth. Run it and point a Streamable-HTTP MCP
// client at the printed URL.

using WebReaper.Mcp.AspNetCore;

var app = McpHttpServer.Build(args);
await app.RunAsync();

// Exposed so the host smoke test can reference the entry assembly.
public partial class Program;
