using System.Security.Cryptography;
using System.Text;

namespace WebReaper.Mcp.AspNetCore;

// ADR-0086: the pure bearer-token decision the auth middleware calls. No
// configured token means auth is disabled (loopback dev; the McpHttpOptions
// bind guard prevents this off-loopback). Otherwise the request must carry
// "Authorization: Bearer <token>" matching in fixed time.
internal static class BearerAuth
{
    public static bool IsAuthorized(string? configuredToken, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(configuredToken)) return true;
        if (string.IsNullOrEmpty(authorizationHeader)) return false;

        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var presented = authorizationHeader[prefix.Length..];
        return FixedTimeEquals(presented, configuredToken);
    }

    // Constant-time within equal lengths; differing lengths short-circuit
    // (leaking only length, which is not the secret).
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
