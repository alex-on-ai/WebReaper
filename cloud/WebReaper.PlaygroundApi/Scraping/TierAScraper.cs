using System.Runtime.CompilerServices;
using WebReaper.Core.Markdown;
using WebReaper.PlaygroundApi.Ssrf;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// Tier A: the ungated, HTTP-only path. Fetch the URL through the
/// <see cref="GuardedHttpClient"/> (SSRF-pinned) and convert to Markdown with
/// the library's <see cref="HtmlToMarkdown"/> primitive (ADR-0063). No browser,
/// no LLM, no climb: a challenge status ends the run with a pointer to the
/// gated full pipeline (Tier B). Emits the shared <c>ClimbEvent</c> shape.
/// </summary>
public sealed class TierAScraper
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    private const long MaxResponseBytes = 5 * 1024 * 1024; // 5 MB

    // Status codes that mean "the server challenged us"; Tier A cannot climb,
    // so these end the run pointing at the gated Tier B.
    private static readonly HashSet<int> ChallengeStatuses = [403, 429, 503];

    public async IAsyncEnumerable<object> StreamAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!RequestUrl.TryNormalizeUrl(url, out var uri))
        {
            yield return ClimbEvents.Error("Enter a valid http(s) URL.");
            yield break;
        }

        yield return ClimbEvents.Request(uri.ToString());
        await Task.Delay(250, cancellationToken);
        yield return ClimbEvents.Attempt("http");

        // The fetch wraps its own try/catch (yield is not allowed inside a
        // try/catch), returning the events to emit for its outcome.
        foreach (var climbEvent in await FetchAsync(uri, cancellationToken))
            yield return climbEvent;
    }

    private static async Task<IReadOnlyList<object>> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var events = new List<object>();
        try
        {
            using var client = GuardedHttpClient.Create(FetchTimeout, MaxResponseBytes);
            using var response = await client.GetAsync(uri, cancellationToken);
            var status = (int)response.StatusCode;

            if (ChallengeStatuses.Contains(status))
            {
                events.Add(ClimbEvents.Blocked("http", status, $"Server returned {status}."));
                events.Add(ClimbEvents.Exhausted(
                    "http",
                    $"This site challenges plain HTTP. Run the full browser and stealth climb locally and free with the CLI: webreaper scrape --stealth {uri}"));
                return events;
            }

            if (!response.IsSuccessStatusCode)
            {
                events.Add(ClimbEvents.Error($"The site responded with HTTP {status}."));
                return events;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            events.Add(ClimbEvents.Success("http", status));

            var content = HtmlToMarkdown.ExtractMainContent(html);
            events.Add(ClimbEvents.Result(content.Title, content.Markdown));
            return events;
        }
        catch (OperationCanceledException)
        {
            events.Add(ClimbEvents.Error("The fetch timed out."));
            return events;
        }
        catch (Exception ex) when (ex is SsrfBlockedException || HasSsrfCause(ex))
        {
            // The guard throws inside SocketsHttpHandler.ConnectCallback, so the
            // refusal usually surfaces wrapped in an HttpRequestException.
            events.Add(ClimbEvents.Error("That URL resolves to a disallowed address and was refused."));
            return events;
        }
        catch (HttpRequestException)
        {
            events.Add(ClimbEvents.Error("Could not reach that URL."));
            return events;
        }
    }

    private static bool HasSsrfCause(Exception exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is SsrfBlockedException)
                return true;
        return false;
    }
}
