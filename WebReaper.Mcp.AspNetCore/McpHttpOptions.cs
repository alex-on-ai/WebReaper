using System.Net;

namespace WebReaper.Mcp.AspNetCore;

// ADR-0086: pure config parse + validate for the HTTP host. No IO: the caller
// passes the token and the configured bind URLs, so this is a closed truth
// table under test (prior art: WebReaper.Cli's EscalationPlan / ScrapeContext).
internal sealed record McpHttpOptions(string? BearerToken, bool RequireAuth)
{
    public const string TokenEnvVar = "WEBREAPER_MCP_TOKEN";

    /// <summary>
    /// Validate the (token, bind-urls) pair into options. Throws an actionable
    /// <see cref="InvalidOperationException"/> when a non-loopback interface is
    /// bound without a token: an unauthenticated, network-reachable scraper is
    /// an SSRF amplifier. Loopback with no token is allowed (local dev).
    /// </summary>
    public static McpHttpOptions Validate(string? bearerToken, IEnumerable<string>? bindUrls)
    {
        var token = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim();
        var bindsNonLoopback = (bindUrls ?? []).Any(u => !IsLoopback(u));

        if (token is null && bindsNonLoopback)
            throw new InvalidOperationException(
                "Refusing to bind a non-loopback interface without authentication. " +
                $"Set the {TokenEnvVar} environment variable to a bearer token, or bind a " +
                "loopback address (localhost / 127.0.0.1) for local development.");

        return new McpHttpOptions(token, RequireAuth: token is not null);
    }

    /// <summary>
    /// A bind URL is loopback when its host is localhost / a loopback IP / ::1.
    /// Kestrel wildcards (0.0.0.0, [::], +, *) and any real host are not.
    /// </summary>
    internal static bool IsLoopback(string url)
    {
        var host = ExtractHost(url);
        if (host is null) return false;
        if (host is "+" or "*" or "0.0.0.0" or "::" or "[::]") return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var bare = host.Trim('[', ']');
        return IPAddress.TryParse(bare, out var ip) && IPAddress.IsLoopback(ip);
    }

    // Pull the host out of [scheme://]host[:port][/path], tolerating IPv6
    // [::1]:port and bare host:port forms.
    private static string? ExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            return close >= 0 ? s[..(close + 1)] : s;
        }

        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        return s.Length == 0 ? null : s;
    }
}
