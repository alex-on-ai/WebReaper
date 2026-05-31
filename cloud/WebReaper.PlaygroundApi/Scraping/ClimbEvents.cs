namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// Factories for the wire events streamed to the climb-viz component. The shape
/// mirrors the front-end `ClimbEvent` union (website/lib/playground/climb-events.ts)
/// exactly, so the same component reducer consumes a recording or this live SSE
/// stream. Serialized camelCase with nulls omitted by the endpoint.
/// </summary>
public static class ClimbEvents
{
    public static object Request(string url) => new { kind = "request", url };

    public static object Attempt(string tier) => new { kind = "attempt", tier };

    public static object Blocked(string tier, int? status, string reason)
        => new { kind = "blocked", tier, status, reason };

    /// <summary>
    /// The climb from one rung to the next. Tier A never climbs, so only Tier B
    /// emits this; <c>from</c> / <c>to</c> are tier names, not indices.
    /// </summary>
    public static object Escalate(string from, string to) => new { kind = "escalate", from, to };

    public static object Success(string tier, int status) => new { kind = "success", tier, status };

    public static object Result(string title, string markdown)
        => new { kind = "result", title, markdown };

    public static object Exhausted(string tier, string reason)
        => new { kind = "exhausted", tier, reason };

    /// <summary>
    /// Not part of the Phase 0 union; the front-end reducer gains an `error`
    /// arm in the wiring checkpoint. Used for invalid input and refused fetches.
    /// </summary>
    public static object Error(string message) => new { kind = "error", message };
}
