using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Domain.Selectors;
using WebReaper.PlaygroundApi.Scraping;
using WebReaper.Sinks.Models;
using Xunit;

namespace WebReaper.PlaygroundApi.Tests;

/// <summary>
/// The Tier B climb-to-wire translation: each <see cref="ClimbStep"/> phase the
/// EscalatingPageLoader emits (ADR-0085) must map to the exact <c>ClimbEvent</c>
/// shape the front-end reducer consumes (website/lib/playground/climb-events.ts).
/// Asserted on the serialized wire JSON (the same camelCase + omit-null options
/// the SSE endpoint uses), so a drift in either the mapping or the serialization
/// is caught.
/// </summary>
public class TierBClimbMappingTests
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonNode WireOf(object climbEvent) =>
        JsonNode.Parse(JsonSerializer.Serialize(climbEvent, Wire))!;

    private static ClimbStep Step(ClimbPhase phase, int tierIndex, int? status = null, string? reason = null) =>
        new(phase, "https://example.com", tierIndex, PageType.Static, status, reason);

    [Fact]
    public void Attempt_maps_to_attempt_with_tier_name()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Attempt, 0))!);
        Assert.Equal("attempt", (string?)w["kind"]);
        Assert.Equal("http", (string?)w["tier"]);
    }

    [Fact]
    public void Attempt_index_one_is_the_browser_rung() =>
        Assert.Equal("browser", (string?)WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Attempt, 1))!)["tier"]);

    [Fact]
    public void Blocked_carries_status_and_reason()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Blocked, 0, status: 403, reason: "Cloudflare challenge"))!);
        Assert.Equal("blocked", (string?)w["kind"]);
        Assert.Equal("http", (string?)w["tier"]);
        Assert.Equal(403, (int?)w["status"]);
        Assert.Equal("Cloudflare challenge", (string?)w["reason"]);
    }

    [Fact]
    public void Blocked_without_status_omits_status_and_defaults_reason()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Blocked, 1))!);
        Assert.Null(w["status"]); // WhenWritingNull => the key is absent, matching TS `status?: number`
        Assert.Equal("Blocked", (string?)w["reason"]);
    }

    [Fact]
    public void Climbing_maps_to_escalate_to_the_next_rung()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Climbing, 0))!);
        Assert.Equal("escalate", (string?)w["kind"]);
        Assert.Equal("http", (string?)w["from"]);
        Assert.Equal("browser", (string?)w["to"]);
    }

    [Fact]
    public void Climbing_from_the_browser_rung_escalates_to_stealth()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Climbing, 1))!);
        Assert.Equal("browser", (string?)w["from"]);
        Assert.Equal("stealth", (string?)w["to"]);
    }

    [Fact]
    public void Succeeded_maps_to_success_with_the_winning_rung_and_status()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Succeeded, 1, status: 200))!);
        Assert.Equal("success", (string?)w["kind"]);
        Assert.Equal("browser", (string?)w["tier"]);
        Assert.Equal(200, (int?)w["status"]);
    }

    [Fact]
    public void Succeeded_without_a_status_defaults_to_200()
    {
        // The wire shape requires a numeric status; a clean browser load that did
        // not surface one is reported as 200.
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Succeeded, 0))!);
        Assert.Equal(200, (int?)w["status"]);
    }

    [Fact]
    public void Exhausted_maps_reason_at_the_top_rung()
    {
        var w = WireOf(ChannelClimbObserver.ToEvent(Step(ClimbPhase.Exhausted, 2, reason: "Still challenged at the top tier"))!);
        Assert.Equal("exhausted", (string?)w["kind"]);
        Assert.Equal("stealth", (string?)w["tier"]);
        Assert.Equal("Still challenged at the top tier", (string?)w["reason"]);
    }

    [Fact]
    public async Task OnStep_writes_the_mapped_event_to_the_channel()
    {
        var channel = Channel.CreateUnbounded<object>();
        var observer = new ChannelClimbObserver(channel.Writer);

        observer.OnStep(Step(ClimbPhase.Attempt, 0));
        channel.Writer.Complete();

        var w = WireOf(await Single(channel.Reader));
        Assert.Equal("attempt", (string?)w["kind"]);
        Assert.Equal("http", (string?)w["tier"]);
    }

    [Fact]
    public async Task ResultSink_emits_a_result_event_with_title_and_markdown()
    {
        var channel = Channel.CreateUnbounded<object>();
        var sink = new MarkdownResultSink(channel.Writer);

        var data = new JsonObject { ["title"] = "Example", ["markdown"] = "# Example\n\nBody." };
        await sink.EmitAsync(new ParsedData("https://example.com", data));
        channel.Writer.Complete();

        var w = WireOf(await Single(channel.Reader));
        Assert.Equal("result", (string?)w["kind"]);
        Assert.Equal("Example", (string?)w["title"]);
        Assert.Equal("# Example\n\nBody.", (string?)w["markdown"]);
    }

    private static async Task<object> Single(ChannelReader<object> reader)
    {
        var events = new List<object>();
        await foreach (var e in reader.ReadAllAsync())
            events.Add(e);
        return Assert.Single(events);
    }
}
