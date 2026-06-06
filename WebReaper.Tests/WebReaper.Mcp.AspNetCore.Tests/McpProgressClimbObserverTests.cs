using ModelContextProtocol;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Domain.Selectors;
using WebReaper.Mcp;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0085 + ADR-0086: the climb-step -> MCP-progress translation, deterministic
// (no engine, no network). The full notifications/progress roundtrip is
// interactive-client behavior the SDK owns; this pins our mapping.
public class McpProgressClimbObserverTests
{
    private sealed class CapturingProgress : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Reports { get; } = [];
        public void Report(ProgressNotificationValue value) => Reports.Add(value);
    }

    private static ClimbStep Step(ClimbPhase phase, string url = "https://example.com/p", int tier = 0) =>
        new(phase, url, tier, PageType.Static, HttpStatus: null, Reason: null);

    [Fact]
    public void Succeeded_steps_increment_the_page_count()
    {
        var captured = new CapturingProgress();
        var observer = new McpProgressClimbObserver(captured, total: 10);

        observer.OnStep(Step(ClimbPhase.Succeeded, "https://example.com/1"));
        observer.OnStep(Step(ClimbPhase.Succeeded, "https://example.com/2"));

        Assert.Equal(2, captured.Reports.Count);
        Assert.Equal(1, (int)captured.Reports[0].Progress);
        Assert.Equal(2, (int)captured.Reports[1].Progress);
        Assert.Equal(10, (int)captured.Reports[1].Total!.Value);
    }

    [Fact]
    public void Attempt_steps_are_not_reported()
    {
        var captured = new CapturingProgress();
        var observer = new McpProgressClimbObserver(captured, total: null);

        observer.OnStep(Step(ClimbPhase.Attempt));

        Assert.Empty(captured.Reports);
    }

    [Fact]
    public void Block_climb_exhaust_report_without_incrementing_the_count()
    {
        var captured = new CapturingProgress();
        var observer = new McpProgressClimbObserver(captured, total: null);

        observer.OnStep(Step(ClimbPhase.Blocked));
        observer.OnStep(Step(ClimbPhase.Climbing));
        observer.OnStep(Step(ClimbPhase.Exhausted));

        Assert.Equal(3, captured.Reports.Count);
        Assert.All(captured.Reports, r => Assert.Equal(0, (int)r.Progress));
    }

    [Fact]
    public void Each_report_carries_a_message_with_the_url()
    {
        var captured = new CapturingProgress();
        var observer = new McpProgressClimbObserver(captured, total: 1);

        observer.OnStep(Step(ClimbPhase.Succeeded, "https://example.com/widget"));

        Assert.Contains("example.com/widget", captured.Reports[0].Message);
    }
}
