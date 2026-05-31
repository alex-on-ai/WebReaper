using System.Threading.Channels;
using WebReaper.Sinks.Abstract;
using WebReaper.Sinks.Models;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// The Tier B result sink. The Markdown extraction (ADR-0040) produces one
/// <see cref="ParsedData"/> for the scraped page, shaped <c>title</c> +
/// <c>markdown</c> (<c>MarkdownContentExtractor</c>); this writes it to the SSE
/// channel as a <c>result</c> ClimbEvent. The climb steps arrive on the
/// <see cref="ChannelClimbObserver"/> (the load stage); the result arrives here
/// (post-extraction), the two-payload split ADR-0085 draws.
/// </summary>
public sealed class MarkdownResultSink : IScraperSink
{
    private readonly ChannelWriter<object> _writer;

    public MarkdownResultSink(ChannelWriter<object> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    // Part of the IScraperSink contract; there is nothing to wipe on start for a
    // channel destination.
    public bool DataCleanupOnStart { get; set; }

    public async Task EmitAsync(ParsedData entity, CancellationToken cancellationToken = default)
    {
        var title = entity.Data["title"]?.GetValue<string>() ?? string.Empty;
        var markdown = entity.Data["markdown"]?.GetValue<string>() ?? string.Empty;
        await _writer.WriteAsync(ClimbEvents.Result(title, markdown), cancellationToken);
    }
}
