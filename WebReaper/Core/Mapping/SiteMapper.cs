using System.Xml.Linq;
using AngleSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebReaper.Core.Mapping;

/// <summary>
/// The default <see cref="ISiteMapper"/> adapter (ADR-0042): URL discovery
/// over <c>robots.txt</c> → <c>sitemap.xml</c> (one level of index
/// recursion) ∪ root-page <c>&lt;a href&gt;</c> extraction. Static HTTP
/// only — JS-rendered link discovery is the consumer's responsibility
/// (build a <c>CrawlWithBrowser</c> Crawl for that). Best-effort: a 404 on
/// <c>/robots.txt</c> or <c>/sitemap.xml</c>, or malformed XML, is logged
/// and skipped; a partial result is more useful than a thrown exception.
/// </summary>
public sealed class SiteMapper : ISiteMapper
{
    private const string UserAgent =
        "Mozilla/5.0 (compatible; WebReaperSiteMapper/1.0; +https://github.com/alex-on-ai/WebReaper)";

    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly ILogger _logger;

    /// <summary>Default — per-call <see cref="HttpClient"/>; no proxies, no
    /// custom cookies.</summary>
    public SiteMapper() : this(() => new HttpClientHandler(), NullLogger.Instance) { }

    /// <summary>
    /// Construct with a custom <paramref name="handlerFactory"/> (proxy
    /// support / test substitution) and an optional
    /// <paramref name="logger"/>. The factory is invoked once per
    /// <see cref="MapAsync"/> call.
    /// </summary>
    public SiteMapper(Func<HttpMessageHandler> handlerFactory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        _handlerFactory = handlerFactory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> MapAsync(
        string url,
        MapOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        options ??= new MapOptions();

        var rootUri = new Uri(url, UriKind.Absolute);

        // One HttpClient per call. The handler factory determines transport
        // (real HttpClientHandler by default, a stub in tests, a
        // proxy/cookie handler in a satellite).
        using var client = new HttpClient(_handlerFactory(), disposeHandler: true);
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

        // The union preserves source order: sitemap URLs first (in
        // discovery order), then root-page link URLs (in DOM order),
        // duplicates collapsed in-favour-of-first-seen.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        if (options.IncludeSitemap)
        {
            foreach (var sitemapUrl in await DiscoverSitemapsAsync(client, rootUri, cancellationToken))
            {
                if (ordered.Count >= options.MaxUrls) break;
                await foreach (var sitemapEntry in ReadSitemapAsync(client, sitemapUrl, cancellationToken))
                {
                    if (ordered.Count >= options.MaxUrls) break;
                    Add(seen, ordered, sitemapEntry);
                }
            }
        }

        if (options.IncludeRootPageLinks && ordered.Count < options.MaxUrls)
        {
            foreach (var link in await ExtractRootPageLinksAsync(client, rootUri, cancellationToken))
            {
                if (ordered.Count >= options.MaxUrls) break;
                Add(seen, ordered, link);
            }
        }

        // Host filter and Search filter applied last — they preserve order
        // and a too-tight Search would otherwise truncate the source pool
        // before the filter even ran.
        IEnumerable<string> result = ordered;

        if (!options.AllowOffsite)
        {
            result = result.Where(u => SameHost(rootUri, u));
        }

        if (!string.IsNullOrEmpty(options.Search))
        {
            var needle = options.Search;
            result = result.Where(u => u.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        return result.Take(options.MaxUrls).ToList();
    }

    private static void Add(HashSet<string> seen, List<string> ordered, string url)
    {
        if (seen.Add(url)) ordered.Add(url);
    }

    private static bool SameHost(Uri root, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return string.Equals(u.Host, root.Host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> DiscoverSitemapsAsync(
        HttpClient client, Uri rootUri, CancellationToken ct)
    {
        var sitemaps = new List<string>();

        // robots.txt for explicit Sitemap: directives — many real sites
        // host their sitemap on a CDN or under a non-standard name.
        try
        {
            var robotsUri = new Uri(rootUri, "/robots.txt");
            var robots = await client.GetStringAsync(robotsUri, ct);
            foreach (var line in robots.Split('\n'))
            {
                var trimmed = line.Trim();
                const string prefix = "Sitemap:";
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var smUrl = trimmed[prefix.Length..].Trim();
                    if (!string.IsNullOrEmpty(smUrl)) sitemaps.Add(smUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "robots.txt fetch failed for {RootUri} — continuing", rootUri);
        }

        if (sitemaps.Count == 0)
        {
            // Convention fallback — the W3C default location.
            sitemaps.Add(new Uri(rootUri, "/sitemap.xml").ToString());
        }

        return sitemaps;
    }

    private async IAsyncEnumerable<string> ReadSitemapAsync(
        HttpClient client,
        string sitemapUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string xml;
        try
        {
            xml = await client.GetStringAsync(sitemapUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Sitemap fetch failed for {SitemapUrl} — skipping", sitemapUrl);
            yield break;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Sitemap parse failed for {SitemapUrl} — skipping", sitemapUrl);
            yield break;
        }

        var root = doc.Root;
        if (root is null) yield break;

        // A sitemap index lists more sitemaps; recurse one level. Deeper
        // nesting is deferred (ADR-0042 Considered options).
        if (root.Name == Sitemap + "sitemapindex")
        {
            var children = root.Elements(Sitemap + "sitemap")
                .Select(s => s.Element(Sitemap + "loc")?.Value)
                .Where(loc => !string.IsNullOrWhiteSpace(loc))
                .Select(loc => loc!.Trim())
                .ToList();

            foreach (var child in children)
            {
                await foreach (var url in ReadChildSitemapAsync(client, child, ct))
                {
                    yield return url;
                }
            }
            yield break;
        }

        // A urlset is the leaf shape — direct URLs.
        if (root.Name == Sitemap + "urlset")
        {
            foreach (var loc in root.Elements(Sitemap + "url")
                .Select(u => u.Element(Sitemap + "loc")?.Value)
                .Where(loc => !string.IsNullOrWhiteSpace(loc)))
            {
                yield return loc!.Trim();
            }
        }
    }

    // Separate method so we can apply a "one level of recursion" bound:
    // a child sitemap is read as urlset only; we do not recurse again
    // into a nested sitemapindex.
    private async IAsyncEnumerable<string> ReadChildSitemapAsync(
        HttpClient client,
        string sitemapUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string xml;
        try
        {
            xml = await client.GetStringAsync(sitemapUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Child sitemap fetch failed for {SitemapUrl} — skipping", sitemapUrl);
            yield break;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Child sitemap parse failed for {SitemapUrl} — skipping", sitemapUrl);
            yield break;
        }

        var root = doc.Root;
        if (root is null || root.Name != Sitemap + "urlset") yield break;

        foreach (var loc in root.Elements(Sitemap + "url")
            .Select(u => u.Element(Sitemap + "loc")?.Value)
            .Where(loc => !string.IsNullOrWhiteSpace(loc)))
        {
            yield return loc!.Trim();
        }
    }

    private async Task<IReadOnlyList<string>> ExtractRootPageLinksAsync(
        HttpClient client, Uri rootUri, CancellationToken ct)
    {
        string html;
        try
        {
            html = await client.GetStringAsync(rootUri, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Root page fetch failed for {RootUri} — skipping", rootUri);
            return Array.Empty<string>();
        }

        // ADR-0036's "tiny AngleSharp query, inlined" pattern. The
        // internal LinkExtractor takes a CSS selector specific to the
        // crawl's chain; here we want every anchor with a usable href.
        var config = Configuration.Default.WithDefaultLoader();
        var context = BrowsingContext.New(config);
        using var doc = await context.OpenAsync(req => req.Content(html));

        return doc
            .QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href =>
            {
                if (Uri.TryCreate(rootUri, href, out var absolute)) return absolute.ToString();
                return null;
            })
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();
    }
}
