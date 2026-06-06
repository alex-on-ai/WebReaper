using ModelContextProtocol;
using WebReaper.Core.Loaders.Abstract;

namespace WebReaper.Mcp;

// ADR-0085 + ADR-0086: forward EscalatingPageLoader climb steps to MCP progress
// notifications, so an interactive MCP client (Claude Desktop, Cursor, Inspector)
// shows live progress during a long crawl or a browser climb. n8n's node is
// blocking and will not render these; this is for clients that do.
//
// OnStep runs on the load hot path and may run concurrently (a sweep loads pages
// under Parallel.ForEachAsync), so the counter is interlocked and IProgress.Report
// is the cheap, non-blocking, thread-safe sink the seam requires; it never throws
// back into the loader.
internal sealed class McpProgressClimbObserver : IClimbObserver
{
    private readonly IProgress<ProgressNotificationValue> _progress;
    private readonly int? _total;
    private int _completed;

    public McpProgressClimbObserver(IProgress<ProgressNotificationValue> progress, int? total)
    {
        _progress = progress;
        _total = total;
    }

    public void OnStep(ClimbStep step)
    {
        switch (step.Phase)
        {
            case ClimbPhase.Succeeded:
                Report(Interlocked.Increment(ref _completed), $"loaded {step.Url}");
                break;
            case ClimbPhase.Climbing:
                Report(_completed, $"climbing to a stronger tier for {step.Url}");
                break;
            case ClimbPhase.Blocked:
                Report(_completed, $"blocked at tier {step.TierIndex}; climbing");
                break;
            case ClimbPhase.Exhausted:
                Report(_completed, $"top tier still blocked for {step.Url}");
                break;
            // Attempt is too noisy to surface.
        }
    }

    private void Report(int done, string message) =>
        _progress.Report(new ProgressNotificationValue
        {
            Progress = done,
            Total = _total,
            Message = message,
        });
}
