namespace WebReaper.Mcp;

// ADR-0086: bound the crawl tool's page count. MCP has no streaming, so an
// unbounded sweep would be one long blocking call that runs past a client's
// node timeout. Small default, hard cap, non-positive rejected.
public static class CrawlBounds
{
    public const int DefaultMaxPages = 50;
    public const int MaxAllowed = 1000;

    /// <summary>Clamp a requested page cap to the allowed range; reject non-positive.</summary>
    public static int Validate(int maxPages)
    {
        if (maxPages <= 0)
            throw new ArgumentException(
                $"max_pages must be positive (default {DefaultMaxPages}, hard cap {MaxAllowed}).",
                nameof(maxPages));
        return Math.Min(maxPages, MaxAllowed);
    }
}
