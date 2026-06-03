namespace WebReaper.PlaygroundApi.Ssrf;

/// <summary>
/// Validates and normalises an inbound scrape URL before it is fetched: it must
/// be a non-empty, absolute http(s) URL (trimmed first). Shared by the Tier A
/// and Tier B scrapers so the request-URL guard lives in one place, not two
/// byte-identical copies. Pairs with <see cref="SsrfPolicy"/>: that guards the
/// resolved address, this guards the URL shape. Public to mirror
/// <see cref="SsrfPolicy"/> and stay directly unit-testable; this app is not a
/// packaged library, so it adds nothing to the WebReaper public surface.
/// </summary>
public static class RequestUrl
{
    /// <summary>
    /// True when <paramref name="url"/> is a non-empty, absolute http(s) URL; on
    /// success <paramref name="uri"/> is the parsed (trimmed) <see cref="Uri"/>,
    /// otherwise it is null.
    /// </summary>
    public static bool TryNormalizeUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = parsed;
        return true;
    }
}
