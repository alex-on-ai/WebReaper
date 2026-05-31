using WebReaper.Domain.Selectors;

namespace WebReaper.Core.Loaders.Abstract;

/// <summary>
/// The phase of a climb step the <c>EscalatingPageLoader</c> reports (ADR-0085).
/// </summary>
public enum ClimbPhase
{
    /// <summary>About to load the page at <see cref="ClimbStep.TierIndex"/>.</summary>
    Attempt,

    /// <summary>The load at this rung was classified as a block.</summary>
    Blocked,

    /// <summary>Climbing from this rung to the next (escalation).</summary>
    Climbing,

    /// <summary>A clean (non-blocked) load; this rung won.</summary>
    Succeeded,

    /// <summary>Still blocked at the top rung; the climb is exhausted.</summary>
    Exhausted,
}

/// <summary>
/// One step of an <c>EscalatingPageLoader</c> climb (ADR-0085): the load-stage
/// progress of one page, namely which rung was tried and whether it blocked,
/// climbed, won, or exhausted. Tier identity is the ladder <see cref="TierIndex"/>
/// (0 = HTTP, then each configured Dynamic rung in registration order); core does
/// not name a rung "browser" or "stealth" (ADR-0009), so a consumer that built the
/// ladder maps the index to a label.
/// </summary>
/// <param name="Phase">The step's phase.</param>
/// <param name="Url">The page URL (lets a concurrent crawl disambiguate interleaved climbs).</param>
/// <param name="TierIndex">The ladder rung this step concerns, lowest = 0. On <see cref="ClimbPhase.Climbing"/> this is the rung being left; the next rung is <c>TierIndex + 1</c>.</param>
/// <param name="PageType">The load mode the rung serves.</param>
/// <param name="HttpStatus">The loaded result's status; null pre-load (<see cref="ClimbPhase.Attempt"/> / <see cref="ClimbPhase.Climbing"/>) or when a transport cannot determine it.</param>
/// <param name="Reason">The block verdict's reason on <see cref="ClimbPhase.Blocked"/> / <see cref="ClimbPhase.Exhausted"/>; null otherwise.</param>
public sealed record ClimbStep(
    ClimbPhase Phase,
    string Url,
    int TierIndex,
    PageType PageType,
    int? HttpStatus,
    string? Reason);

/// <summary>
/// A sink for the <see cref="ClimbStep"/>s an <c>EscalatingPageLoader</c> emits as
/// it climbs (ADR-0085). The default is the no-op <c>NullClimbObserver</c>; a
/// consumer (the cloud playground's live SSE stream, the CLI's <c>--progress json</c>)
/// wires its own to observe the climb without scraping logs.
/// </summary>
/// <remarks>
/// <see cref="OnStep"/> is called on the load hot path, possibly concurrently (a
/// crawl loads pages under <c>Parallel.ForEachAsync</c>). Implementations must be
/// cheap, non-blocking, and thread-safe (for example a channel write), and must
/// not throw back into the loader.
/// </remarks>
public interface IClimbObserver
{
    /// <summary>Report one climb step. Must not throw.</summary>
    void OnStep(ClimbStep step);
}
