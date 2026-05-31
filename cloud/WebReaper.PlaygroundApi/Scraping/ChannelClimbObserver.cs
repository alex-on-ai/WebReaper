using System.Threading.Channels;
using WebReaper.Core.Loaders.Abstract;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// Bridges the core <see cref="IClimbObserver"/> (ADR-0085) to the playground's
/// SSE wire shape. Each <see cref="ClimbStep"/> the <c>EscalatingPageLoader</c>
/// emits as it climbs is mapped to a <c>ClimbEvent</c> and written to the bounded
/// channel the Tier B SSE endpoint drains. The backend owns the rung
/// index-to-name map because it built the ladder and core never names a rung
/// "stealth" (ADR-0009): 0 = http, 1 = browser, 2 = stealth.
/// </summary>
public sealed class ChannelClimbObserver : IClimbObserver
{
    // The ladder TierBScraper composes: the HTTP rung, then the vanilla browser
    // rung, then (build-order step 3) the stealth rung. The index is the rung
    // identity carried on ClimbStep.TierIndex.
    private static readonly string[] TierNames = ["http", "browser", "stealth"];

    private readonly ChannelWriter<object> _writer;

    public ChannelClimbObserver(ChannelWriter<object> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Non-blocking and never throws, per the ADR-0085 <c>OnStep</c> contract: a
    /// bounded-channel <see cref="ChannelWriter{T}.TryWrite"/> returns false
    /// rather than awaiting if the reader ever falls behind (it does not in
    /// practice: a climb emits a handful of steps and the SSE reader drains
    /// immediately). An unmapped phase is dropped, not thrown.
    /// </remarks>
    public void OnStep(ClimbStep step)
    {
        if (ToEvent(step) is { } climbEvent)
            _writer.TryWrite(climbEvent);
    }

    /// <summary>
    /// Map one <see cref="ClimbStep"/> to its <c>ClimbEvent</c> wire object (the
    /// shape in <c>website/lib/playground/climb-events.ts</c>). Pure, so the
    /// translation and the index-to-name map are unit-testable without a channel
    /// or a running engine. Returns <c>null</c> for an unknown phase.
    /// </summary>
    public static object? ToEvent(ClimbStep step) => step.Phase switch
    {
        ClimbPhase.Attempt => ClimbEvents.Attempt(TierName(step.TierIndex)),
        ClimbPhase.Blocked => ClimbEvents.Blocked(TierName(step.TierIndex), step.HttpStatus, step.Reason ?? "Blocked"),
        ClimbPhase.Climbing => ClimbEvents.Escalate(TierName(step.TierIndex), TierName(step.TierIndex + 1)),
        // Succeeded is the first time core reports the winning rung and its
        // status. The wire shape requires a numeric status; a clean load that did
        // not surface one (some browser loads) is reported as 200.
        ClimbPhase.Succeeded => ClimbEvents.Success(TierName(step.TierIndex), step.HttpStatus ?? 200),
        ClimbPhase.Exhausted => ClimbEvents.Exhausted(TierName(step.TierIndex), step.Reason ?? "Still blocked at the top tier"),
        _ => null,
    };

    private static string TierName(int index) =>
        index >= 0 && index < TierNames.Length ? TierNames[index] : $"tier{index}";
}
