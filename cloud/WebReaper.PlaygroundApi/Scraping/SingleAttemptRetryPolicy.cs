using WebReaper.Infra.Abstract;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// A no-retry <see cref="IRetryPolicy"/>: runs the action exactly once and lets
/// every exception (cancellation included) propagate. The Tier B playground
/// scrapes a single URL whose <c>EscalatingPageLoader</c> already climbs
/// HTTP -&gt; vanilla -&gt; stealth internally on each block, so the core default's
/// whole-crawl retry (four attempts) replays the entire multi-minute browser
/// climb from the lifted host floor on any transient transport throw -- the
/// duplicate <c>attempt(browser)</c> observed in the live climb. One attempt is
/// the right bound here; the climb's per-rung waits are the resilience.
/// </summary>
public sealed class SingleAttemptRetryPolicy : IRetryPolicy
{
    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return action(cancellationToken);
    }
}
