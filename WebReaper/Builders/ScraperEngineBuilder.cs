using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using WebReaper.ConfigStorage.Abstract;
using WebReaper.ConfigStorage.Concrete;
using WebReaper.Core;
using WebReaper.Core.Actions.Abstract;
using WebReaper.Core.Actions.Concrete;
using WebReaper.Core.Blocking.Abstract;
using WebReaper.Core.CookieStorage.Abstract;
using WebReaper.Core.LinkTracker.Abstract;
using WebReaper.Core.LinkTracker.Concrete;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Core.Loaders.Concrete;
using WebReaper.Core.Mapping;
using WebReaper.Core.Parser.Abstract;
using WebReaper.Core.Parser.Concrete;
using WebReaper.Core.Scheduler.Abstract;
using WebReaper.Core.Scheduler.Concrete;
using WebReaper.Core.Spider.Abstract;
using WebReaper.Domain;
using WebReaper.Domain.PageActions;
using WebReaper.Domain.Parsing;
using WebReaper.Infra.Abstract;
using WebReaper.Logging;
using WebReaper.Processing;
using WebReaper.Processing.Abstract;
using WebReaper.Processing.Concrete;
using WebReaper.Proxy;
using WebReaper.Proxy.Abstract;
using WebReaper.Sinks.Abstract;
using WebReaper.Sinks.Concrete;
using WebReaper.Sinks.Models;

namespace WebReaper.Builders;

/// <summary>
/// The public fluent builder for configuring and building a scraper engine
/// (ADR-0025). A scrape begins with a <b>Crawl seed</b>: obtain one from the
/// static <see cref="Crawl(string[])"/> / <see cref="CrawlWithBrowser(string[])"/>,
/// give it a <see cref="Schema"/> via <see cref="ICrawlSeed.Extract"/>, and
/// you get this builder — every free fluent method and every satellite
/// extension — terminating in <see cref="BuildAsync"/> (a runnable engine
/// plus its persisted config) or <see cref="Build"/> (just the immutable
/// <see cref="ScraperConfig"/>, e.g. for a distributed start endpoint to
/// persist). The constructor is internal and the terminals live only here, so
/// "build with no start URLs or no schema" is unrepresentable. The
/// distributed-worker reduced shell (ADR-0009) is a separate seam —
/// <see cref="DistributedSpiderBuilder"/>. Composes an internal
/// <c>ConfigBuilder</c> and <c>SpiderBuilder</c>, all with in-memory defaults.
/// </summary>
public class ScraperEngineBuilder
{
    private IVisitedLinkTracker _visitedLinksTracker = new InMemoryVisitedLinkTracker();
    private int _parallelismDegree = 20;
    private ConfigBuilder ConfigBuilder { get; } = new();
    private SpiderBuilder SpiderBuilder { get; } = new();

    // ADR-0081: Site-sweep sitemap seeding. _sweepSitemap is set true by
    // .Sweep(options) when SweepOptions.Sitemap is on; BuildAsync then unions
    // the Site mapper's discovered URLs into the start set. The mapper is
    // injectable for offline tests (WithSiteMapper).
    private bool _sweepSitemap;
    private ISiteMapper _siteMapper = new SiteMapper();

    // ADR-0072: the shared extractor + validator + wrapping-logic module
    // both this builder and AgentEngineBuilder embed. Logger is read via
    // a getter lambda so WithLogger updates after the field-initializer
    // run still flow through to wrappers constructed by
    // WithFallbackExtractor / WithSelfHealing.
    private readonly ContentExtractorPipeline _pipeline;

    /// <summary>
    /// Per-run telemetry hooks (ADR-0066). Set by satellites at
    /// <c>WithLlm*</c> / <c>.UseAi(...)</c> time to register a
    /// per-builder telemetry accumulator; consumed by
    /// <see cref="BuildAsync"/> to pass to the constructed engine.
    /// <c>null</c> when no satellite has registered telemetry — the
    /// engine returns <see cref="WebReaper.Domain.Telemetry.RunReport"/>
    /// with <c>Llm == null</c>.
    /// <para>
    /// This is a satellite hook (the ADR-0058 <c>OnTeardown</c>
    /// pattern). Most consumers should not set it directly; the
    /// satellite's <c>WithLlm*</c> / <c>.UseAi(...)</c> extensions do
    /// it for them.
    /// </para>
    /// </summary>
    public Domain.Telemetry.RunTelemetryHooks? TelemetryHooks { get; set; }

    /// <summary>Internal: a <see cref="ScraperEngineBuilder"/> is obtained only
    /// via <see cref="Crawl(string[])"/> / <see cref="CrawlWithBrowser(string[])"/>
    /// then <see cref="ICrawlSeed.Extract"/> — never <c>new</c>ed. This is the
    /// structural guarantee (ADR-0025): the build terminals cannot be reached
    /// without a Crawl seed and a schema.</summary>
    internal ScraperEngineBuilder()
    {
        _pipeline = new ContentExtractorPipeline(() => Logger);
    }

    /// <summary>Internal accessor for the shared extractor pipeline — used
    /// by <see cref="ICrawlSeed"/> implementations (the <c>AsMarkdown()</c>
    /// and other terminals) to register their seed extractor via the
    /// pipeline instead of bypassing it.</summary>
    internal ContentExtractorPipeline Pipeline => _pipeline;

    /// <summary>
    /// Discover the URLs of a site without running a Crawl (ADR-0042 —
    /// firecrawl-shaped <c>/map</c>): sugar over
    /// <c>new SiteMapper().MapAsync(url, options, ct)</c>. Returns the union
    /// of <c>robots.txt</c>-declared sitemap entries (one level of index
    /// recursion) and the root page's <c>&lt;a href&gt;</c> URLs, host-
    /// filtered, deduplicated, ordered sitemap-then-root-page-links.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="url"/> is
    /// null/empty/whitespace.</exception>
    public static Task<IReadOnlyList<string>> MapAsync(
        string url,
        MapOptions? options = null,
        CancellationToken cancellationToken = default)
        => new SiteMapper().MapAsync(url, options, cancellationToken);

    /// <summary>
    /// Begin a scrape: the crawl's start URLs, loaded as static HTTP pages
    /// (the ADR-0025 Crawl seed). Returns an <see cref="ICrawlSeed"/> whose
    /// terminals are <see cref="ICrawlSeed.Extract"/> and
    /// <see cref="ICrawlSeed.AsMarkdown"/>.
    /// </summary>
    /// <exception cref="ArgumentException">no start URL was supplied
    /// (fail-fast).</exception>
    public static ICrawlSeed Crawl(params string[] startUrls)
    {
        if (startUrls is null || startUrls.Length == 0)
            throw new ArgumentException(
                "At least one start URL is required.", nameof(startUrls));
        var builder = new ScraperEngineBuilder();
        builder.ConfigBuilder.Get(startUrls);
        return new CrawlSeed(builder);
    }

    /// <summary>
    /// Begin a scrape whose start pages load through the headless-browser
    /// transport (the ADR-0025 Crawl seed; requires the WebReaper.Puppeteer
    /// satellite at run time, ADR-0009).
    /// </summary>
    /// <exception cref="ArgumentException">no start URL was supplied
    /// (fail-fast).</exception>
    public static ICrawlSeed CrawlWithBrowser(params string[] startUrls)
    {
        if (startUrls is null || startUrls.Length == 0)
            throw new ArgumentException(
                "At least one start URL is required.", nameof(startUrls));
        var builder = new ScraperEngineBuilder();
        builder.ConfigBuilder.GetWithBrowser(startUrls);
        return new CrawlSeed(builder);
    }

    /// <summary>
    /// <see cref="CrawlWithBrowser(string[])"/> with an optional
    /// <see cref="PageActionBuilder"/> run on each start page before scraping.
    /// </summary>
    /// <exception cref="ArgumentException">no start URL was supplied
    /// (fail-fast).</exception>
    public static ICrawlSeed CrawlWithBrowser(
        IEnumerable<string> startUrls,
        Func<PageActionBuilder, List<PageAction>>? actionBuilder = null)
    {
        var urls = startUrls?.ToArray() ?? Array.Empty<string>();
        if (urls.Length == 0)
            throw new ArgumentException(
                "At least one start URL is required.", nameof(startUrls));
        var builder = new ScraperEngineBuilder();
        builder.ConfigBuilder.GetWithBrowser(urls, actionBuilder?.Invoke(new PageActionBuilder()));
        return new CrawlSeed(builder);
    }

    /// <summary>The <see cref="ICrawlSeed"/> implementation: holds the
    /// half-built builder so the only operations reachable after
    /// <see cref="Crawl(string[])"/> are the strategy terminals
    /// (<see cref="ICrawlSeed.Extract"/>, <see cref="ICrawlSeed.AsMarkdown"/>).</summary>
    private sealed class CrawlSeed : ICrawlSeed
    {
        private readonly ScraperEngineBuilder _builder;
        internal CrawlSeed(ScraperEngineBuilder builder) => _builder = builder;

        public ScraperEngineBuilder Extract(Schema schema)
        {
            _builder.ConfigBuilder.WithScheme(schema);
            return _builder;
        }

        public ScraperEngineBuilder AsMarkdown()
        {
            // ADR-0040: the no-schema strategy terminal. Leaves
            // ConfigBuilder._schema null (Spider/CrawlStep/Config are
            // already null-tolerant — ADR-0008 / ADR-0025 read-side
            // wiring) and registers the Markdown extractor on the
            // ADR-0039 IContentExtractor seam. ADR-0072: registration
            // goes through the shared pipeline, not directly to
            // SpiderBuilder — keeps a single source of truth for the
            // current extractor.
            _builder.Pipeline.WithContentExtractor(new MarkdownContentExtractor());
            return _builder;
        }

        public ScraperEngineBuilder ExtractInferred(string? goal = null)
        {
            // ADR-0067: the runtime-schema-inference terminal. Like
            // AsMarkdown the ConfigBuilder schema stays null (the inferred
            // schema lives on the wrapper, not the config). The marker is
            // resolved at BuildAsync time — if no real ISchemaInferrer was
            // registered the build throws with an actionable message.
            // Wrapping the inner extractor with LearnedSchemaContentExtractor
            // is deferred until BuildAsync so consumers can call this
            // terminal before .WithSchemaInferrer / .WithLlmSchemaInferrer.
            _builder._inferenceMarker = new InferenceMarker(goal);
            return _builder;
        }
    }

    // ADR-0067: marker placed by ICrawlSeed.ExtractInferred; resolved in
    // BuildAsync. Goal threads through to LearnedSchemaContentExtractor.
    private sealed record InferenceMarker(string? Goal);

    private InferenceMarker? _inferenceMarker;
    private ISchemaInferrer _schemaInferrer = NullSchemaInferrer.Instance;

    // ADR-0069: validator-driven re-inference thresholds. Defaults preserve
    // ADR-0067 v1 trust-the-cache behaviour (reInferAfterFailures = 0 =
    // never re-infer). The satellite's WithLlmSchemaInferrer threads
    // LlmSchemaInferrerOptions.ReInferAfterFailures /
    // MaxReInferencesPerInstance into these via
    // WithSchemaInferenceTriggers; consumers wiring a custom inferrer
    // call WithSchemaInferenceTriggers directly to opt in.
    private int _reInferAfterFailures;
    private int _maxReInferencesPerInstance = int.MaxValue;

    // ADR-0067 test seam — exposes the marker state for the unit tests
    // without leaking the InferenceMarker record. Returns
    // <c>(false, null)</c> when ExtractInferred was not called.
    internal (bool Marked, string? Goal) InferenceMarkerForTests
        => (_inferenceMarker is not null, _inferenceMarker?.Goal);

    // ADR-0067 test seam — the registered inferrer (NullSchemaInferrer.Instance
    // by default).
    internal ISchemaInferrer SchemaInferrerForTests => _schemaInferrer;

    // ADR-0069 test seam — the configured re-inference trigger thresholds.
    internal (int ReInferAfterFailures, int MaxReInferencesPerInstance)
        SchemaInferenceTriggersForTests
        => (_reInferAfterFailures, _maxReInferencesPerInstance);

    private ILogger Logger { get; set; } = NullLogger.Instance;

    private IScheduler Scheduler { get; set; } = new InMemoryScheduler();
    // Non-nullable — default is the in-memory adapter; WithConfigStorage
    // takes a non-nullable IScraperConfigStorage so the property can
    // never be null in normal use.
    private IScraperConfigStorage ConfigStorage { get; set; } = new InMemoryScraperConfigStorage();
    /// <summary>The proxy provider, once configured via
    /// <see cref="WithProxies"/> / <see cref="WithValidatedProxies(IProxySource, IEnumerable{IProxyValidator}, ValidatedProxyProviderOptions)"/>.</summary>
    protected IProxyProvider? ProxyProvider { get; set; }

    /// <summary>Register a custom <see cref="IContentExtractor"/> — an
    /// alternative extraction strategy (e.g. an LLM-backed extractor), or the
    /// deterministic <c>SchemaFold</c> over a custom <c>ISchemaBackend</c>
    /// (ADR-0002 / ADR-0039) — instead of the default AngleSharp/CSS fold.</summary>
    public ScraperEngineBuilder WithContentExtractor(IContentExtractor extractor)
    {
        _pipeline.WithContentExtractor(extractor);
        return this;
    }

    /// <summary>
    /// Compose the currently-registered (or default <c>SchemaFold</c>)
    /// content extractor with a <paramref name="fallback"/> via an
    /// <see cref="WebReaper.Core.Parser.Concrete.ExtractionRouter"/>
    /// (ADR-0046): run the primary first; on validation failure
    /// (default: any required schema leaf empty or absent), escalate to
    /// the fallback. The deterministic-first → LLM-fallback wedge.
    /// <para>
    /// The validator is the builder-registered <see cref="WebReaper.Core.Parser.Abstract.ISchemaValidator"/>
    /// (ADR-0062 — register via <see cref="WithSchemaValidator"/>) or the
    /// default <see cref="WebReaper.Core.Parser.Concrete.SchemaSatisfiedValidator"/>.
    /// </para>
    /// </summary>
    /// <param name="fallback">The fallback extractor — typically an
    /// LLM (ADR-0044) but any <see cref="IContentExtractor"/>
    /// works.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fallback"/> is null.</exception>
    public ScraperEngineBuilder WithFallbackExtractor(IContentExtractor fallback)
    {
        _pipeline.WithFallbackExtractor(fallback);
        return this;
    }

    /// <summary>
    /// Wrap the currently-registered (or default <c>SchemaFold</c>)
    /// extractor with a <see cref="WebReaper.Core.Parser.Concrete.SelfHealingContentExtractor"/>
    /// (ADR-0047): on a failed deterministic pass, ask the
    /// <paramref name="repairer"/> for a patched Schema, validate it
    /// by re-running the fold, and cache the patch for every
    /// subsequent page of the Crawl. The LLM-as-proposer / fold-as-
    /// validator wedge.
    /// <para>
    /// The validator is the builder-registered <see cref="WebReaper.Core.Parser.Abstract.ISchemaValidator"/>
    /// (ADR-0062 — register via <see cref="WithSchemaValidator"/>) or the
    /// default <see cref="WebReaper.Core.Parser.Concrete.SchemaSatisfiedValidator"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="repairer"/> is null.</exception>
    public ScraperEngineBuilder WithSelfHealing(WebReaper.Core.Parser.Abstract.ISelectorRepairer repairer)
    {
        _pipeline.WithSelfHealing(repairer);
        return this;
    }

    /// <summary>
    /// Register a custom <see cref="WebReaper.Core.Parser.Abstract.ISchemaValidator"/>
    /// (ADR-0062) — the verdict source for both
    /// <see cref="WithFallbackExtractor"/> (ADR-0046 routing) and
    /// <see cref="WithSelfHealing"/> (ADR-0047 repair). Defaults to
    /// <see cref="WebReaper.Core.Parser.Concrete.SchemaSatisfiedValidator"/>
    /// (the ADR-0029-aligned "every required leaf non-empty" rule —
    /// integer 0 / boolean false count as valid).
    /// <para>
    /// The validator's policy is global to the build — same instance
    /// for every site. Per-schema specialisation is a v2 deferral. To
    /// force-pass or force-fail in tests, implement
    /// <see cref="WebReaper.Core.Parser.Abstract.ISchemaValidator"/>
    /// directly with the desired verdict.
    /// </para>
    /// <para>
    /// Call <em>before</em> <see cref="WithFallbackExtractor"/> /
    /// <see cref="WithSelfHealing"/> — the wrapper composes against
    /// whatever validator is registered at that moment.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="validator"/> is null.</exception>
    public ScraperEngineBuilder WithSchemaValidator(
        WebReaper.Core.Parser.Abstract.ISchemaValidator validator)
    {
        _pipeline.WithSchemaValidator(validator);
        return this;
    }

    /// <summary>
    /// Register a custom <see cref="ISchemaInferrer"/> (ADR-0067) — the
    /// engine consults it on the first page of a crawl that chose the
    /// <see cref="ICrawlSeed.ExtractInferred(string?)"/> seed terminal,
    /// caches the proposed <see cref="Schema"/> on the
    /// <see cref="WebReaper.Core.Parser.Concrete.LearnedSchemaContentExtractor"/>
    /// wrapper, and runs the deterministic fold against it for every
    /// subsequent page.
    /// <para>
    /// For the LLM-backed default, prefer the satellite's one-liner
    /// <c>.WithLlmSchemaInferrer(chatClient)</c> — same wiring with the
    /// telemetry handle and per-role caching policy hooked up.
    /// </para>
    /// <para>
    /// Silently ignored when the consumer chose
    /// <see cref="ICrawlSeed.Extract"/> or
    /// <see cref="ICrawlSeed.AsMarkdown"/> instead — the
    /// <c>LearnedSchemaContentExtractor</c> wrapper is only composed when
    /// <c>ExtractInferred</c> was called.
    /// </para>
    /// </summary>
    /// <param name="inferrer">The schema inferrer.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="inferrer"/> is null.</exception>
    public ScraperEngineBuilder WithSchemaInferrer(ISchemaInferrer inferrer)
    {
        ArgumentNullException.ThrowIfNull(inferrer);
        _schemaInferrer = inferrer;
        return this;
    }

    /// <summary>
    /// Configure validator-driven re-inference triggers for the
    /// <see cref="WebReaper.Core.Parser.Concrete.LearnedSchemaContentExtractor"/>
    /// wrapper (ADR-0069). The wrapper consults the builder-registered
    /// <see cref="WebReaper.Core.Parser.Abstract.ISchemaValidator"/>
    /// (ADR-0062 — default <see cref="WebReaper.Core.Parser.Concrete.SchemaSatisfiedValidator"/>)
    /// on every extraction; <paramref name="reInferAfterFailures"/>
    /// consecutive invalid verdicts drop the cached inferred schema and
    /// trigger re-inference on the next call.
    /// <para>
    /// Silently ignored when the consumer chose
    /// <see cref="ICrawlSeed.Extract"/> or
    /// <see cref="ICrawlSeed.AsMarkdown"/> instead of
    /// <see cref="ICrawlSeed.ExtractInferred"/>.
    /// </para>
    /// <para>
    /// The satellite extension
    /// <c>WithLlmSchemaInferrer(IChatClient, LlmSchemaInferrerOptions?)</c>
    /// calls this method internally with the values from
    /// <c>LlmSchemaInferrerOptions.ReInferAfterFailures</c> +
    /// <c>MaxReInferencesPerInstance</c> — most consumers do not call
    /// this method directly. Call it when wiring a custom
    /// <see cref="WebReaper.Core.Parser.Abstract.ISchemaInferrer"/> or
    /// to override the satellite's defaults.
    /// </para>
    /// </summary>
    /// <param name="reInferAfterFailures">Number of consecutive validator
    /// failures before the cached schema is dropped. <c>0</c> = never
    /// re-infer (preserves the ADR-0067 v1 trust-the-cache behaviour
    /// for consumers wiring a custom inferrer; the satellite's
    /// <c>LlmSchemaInferrerOptions.ReInferAfterFailures</c> defaults
    /// to <c>3</c>).</param>
    /// <param name="maxReInferencesPerInstance">Cost cap — once the
    /// wrapper has re-inferred this many times on the same instance,
    /// further failures keep the stale schema and log at Warning.
    /// Default <see cref="int.MaxValue"/> (unbounded; the cap is the
    /// consumer's guardrail).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reInferAfterFailures"/> or
    /// <paramref name="maxReInferencesPerInstance"/> is negative.</exception>
    public ScraperEngineBuilder WithSchemaInferenceTriggers(
        int reInferAfterFailures,
        int maxReInferencesPerInstance = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reInferAfterFailures);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReInferencesPerInstance);
        _reInferAfterFailures = reInferAfterFailures;
        _maxReInferencesPerInstance = maxReInferencesPerInstance;
        return this;
    }

    /// <summary>
    /// Track changes across crawls (ADR-0048): hash the Markdown
    /// extraction of every page, compare to the previously-stored
    /// hash for the URL, and annotate every emitted record with a
    /// <c>change_status</c> field of <c>"new"</c>, <c>"same"</c>, or
    /// <c>"changed"</c> (plus <c>previous_hash</c> when not new).
    /// </summary>
    /// <param name="store">Optional custom <see cref="WebReaper.Processing.Abstract.IChangeStore"/>;
    /// defaults to <see cref="WebReaper.Processing.Concrete.InMemoryChangeStore"/>.</param>
    public ScraperEngineBuilder WithChangeTracking(
        WebReaper.Processing.Abstract.IChangeStore? store = null)
    {
        SpiderBuilder.AddProcessor(
            new WebReaper.Processing.Concrete.ChangeTrackingProcessor(
                store ?? new WebReaper.Processing.Concrete.InMemoryChangeStore()));
        return this;
    }

    /// <summary>
    /// Parse responses as JSON instead of HTML (issue #27). Schema
    /// selectors become JSONPath expressions.
    /// </summary>
    public ScraperEngineBuilder WithJsonContentParser()
    {
        SpiderBuilder.WithJsonContentParser();
        return this;
    }

    /// <summary>
    /// Parse responses as HTML with XPath 1.0 selectors instead of CSS
    /// (discussion #17). Same shared Schema fold, different backend
    /// (ADR 0002 / 0007).
    /// </summary>
    public ScraperEngineBuilder WithXPathContentParser()
    {
        SpiderBuilder.WithXPathContentParser();
        return this;
    }

    /// <summary>Add a custom <see cref="IScraperSink"/>. Sinks compose — every
    /// registered sink receives every scraped record.</summary>
    public ScraperEngineBuilder AddSink(IScraperSink sink)
    {
        SpiderBuilder.AddSink(sink);
        return this;
    }

    /// <summary>Seed the crawl's cookie container (e.g. an authenticated
    /// session) before any page loads — see <see cref="ICookiesStorage"/>.</summary>
    public ScraperEngineBuilder SetCookies(Action<CookieContainer> authorize)
    {
        SpiderBuilder.SetCookies(authorize);
        return this;
    }

    /// <summary>URLs the crawl must never enqueue (a discovery-time
    /// blocklist).</summary>
    public ScraperEngineBuilder IgnoreUrls(params string[] urls)
    {
        ConfigBuilder.IgnoreUrls(urls);
        return this;
    }

    /// <summary>Soft cap on pages crawled (ADR-0022: best-effort — in-flight
    /// pages still finish, so it can overshoot by ~the parallelism degree).</summary>
    public ScraperEngineBuilder PageCrawlLimit(int limit)
    {
        ConfigBuilder.WithPageCrawlLimit(limit);
        return this;
    }

    /// <summary>
    /// Stop the engine once all discovered links have been crawled
    /// (issue #20), so <c>await engine.RunAsync()</c> actually returns
    /// for finite crawls. Uses the in-memory scheduler.
    /// </summary>
    public ScraperEngineBuilder StopWhenAllLinksProcessed()
    {
        ConfigBuilder.StopWhenAllLinksProcessed();
        return this;
    }

    /// <summary>Run the browser headed (<c>false</c>) or headless
    /// (<c>true</c>, default) for dynamic pages.</summary>
    public ScraperEngineBuilder HeadlessMode(bool headless)
    {
        ConfigBuilder.HeadlessMode(headless);
        return this;
    }

    /// <summary>Use a custom <see cref="IVisitedLinkTracker"/> (the
    /// idempotency authority, ADR-0022) instead of the in-memory default.</summary>
    public ScraperEngineBuilder WithLinkTracker(IVisitedLinkTracker linkTracker)
    {
        SpiderBuilder.WithLinkTracker(linkTracker);
        return this;
    }

    /// <summary>
    /// Track visited links in a file so a crawl resumes across restarts.
    /// </summary>
    /// <param name="fileName">Path of the visited-links file.</param>
    /// <param name="dataCleanupOnStart">Wipe the file on start (fresh run)
    /// rather than resume; defaults to <c>false</c> (resume).</param>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is
    /// null/empty/whitespace (8.0.0 fail-fast).</exception>
    public ScraperEngineBuilder TrackVisitedLinksInFile(string fileName, bool dataCleanupOnStart = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        _visitedLinksTracker = new FileVisitedLinkedTracker(fileName, dataCleanupOnStart);
        SpiderBuilder.WithLinkTracker(_visitedLinksTracker);
        return this;
    }

    /// <summary>Route library logging to your <see cref="ILogger"/>.</summary>
    public ScraperEngineBuilder WithLogger(ILogger logger)
    {
        SpiderBuilder.WithLogger(logger);

        Logger = logger;

        return this;
    }

    /// <summary>Log to the console with the built-in colour logger.</summary>
    public ScraperEngineBuilder LogToConsole()
    {
        SpiderBuilder.WithLogger(new ColorConsoleLogger());

        return this;
    }

    /// <summary>Write every scraped record to the console (a built-in
    /// <see cref="IScraperSink"/>).</summary>
    public ScraperEngineBuilder WriteToConsole()
    {
        SpiderBuilder.WriteToConsole();
        return this;
    }


    /// <summary>
    /// Invoke <paramref name="scrapingResultHandler"/> for every scraped
    /// record. ADR-0038: sugar for registering a delegate
    /// <see cref="IScraperSink"/> — an in-process delegate destination, not a
    /// separate notification seam; it composes with the other sinks and
    /// receives its own clone of the record, like any sink. To react to a page
    /// <em>before</em> it reaches the sinks (enrich / filter / repair), use
    /// <see cref="Process(IPageProcessor)"/> instead.
    /// </summary>
    public ScraperEngineBuilder Subscribe(Action<ParsedData> scrapingResultHandler)
    {
        SpiderBuilder.AddSink(new DelegateSink(scrapingResultHandler));
        return this;
    }

    /// <summary>
    /// Write scraped records to a CSV file.
    /// </summary>
    /// <param name="filePath">Path of the CSV file.</param>
    /// <param name="dataCleanupOnStart">Wipe the file on start vs append.
    /// Required — no default (contrast <see cref="WriteToJsonFile"/>, whose
    /// default is <c>true</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is
    /// null/empty/whitespace.</exception>
    public ScraperEngineBuilder WriteToCsvFile(string filePath, bool dataCleanupOnStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        SpiderBuilder.WriteToCsvFile(filePath, dataCleanupOnStart);
        return this;
    }

    /// <summary>
    /// Write scraped records to a file as <b>JSON Lines</b> — one JSON object
    /// per line, not a JSON array.
    /// </summary>
    /// <param name="filePath">Path of the JSON Lines file.</param>
    /// <param name="dataCleanupOnStart">Wipe the file on start. Defaults to
    /// <c>true</c> (fresh file each run) — the opposite of the other file
    /// sinks; pass <c>false</c> to append across runs.</param>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is
    /// null/empty/whitespace.</exception>
    public ScraperEngineBuilder WriteToJsonFile(string filePath, bool dataCleanupOnStart = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        SpiderBuilder.WriteToJsonFile(filePath, dataCleanupOnStart);
        return this;
    }

    /// <summary>Route page loads through a rotating proxy
    /// (<see cref="IProxyProvider"/>).</summary>
    public ScraperEngineBuilder WithProxies(IProxyProvider proxyProvider)
    {
        SpiderBuilder.WithProxies(proxyProvider);
        return this;
    }

    /// <summary>
    /// Register the transport used for Dynamic (headless-browser) pages
    /// (ADR-0009). Core is HTTP-only by default; the headless-browser
    /// transport lives in the WebReaper.Puppeteer satellite — add that
    /// package and call <c>.WithPuppeteerPageLoader()</c> (which calls this
    /// seam). The factory is invoked at build time with the builder's
    /// resolved cookie storage, optional proxy provider and logger, so the
    /// pre-7.0 default behaviour is preserved exactly. ADR-0004's
    /// one-<see cref="IPageLoader"/> / two-<see cref="IPageLoadTransport"/>
    /// dispatcher is unchanged.
    /// </summary>
    public ScraperEngineBuilder WithLoadTransport(
        Func<ICookiesStorage, IProxyProvider?, ILogger, IActionResolver, IPageLoadTransport> dynamicTransportFactory)
    {
        SpiderBuilder.WithLoadTransport(dynamicTransportFactory);
        return this;
    }

    /// <summary>
    /// Register the <see cref="IActionResolver"/> the Puppeteer transport
    /// invokes for <see cref="PageAction.SemanticAct"/> arms (ADR-0050) —
    /// resolving the natural-language intent to a concrete arm at runtime, then
    /// caching the resolution per crawl. Default is <see cref="NullActionResolver"/>:
    /// dispatching a <c>SemanticAct</c> throws
    /// <see cref="SemanticActResolutionException"/>, and a warning fires on
    /// <see cref="BuildAsync"/> when the config contains any
    /// <c>SemanticAct</c>. The LLM-backed implementation ships in the
    /// <c>WebReaper.AI</c> satellite as <c>LlmActionResolver</c> — reach it
    /// directly via the satellite's <c>WithLlmActionResolver</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolver"/> is null.</exception>
    public ScraperEngineBuilder WithActionResolver(IActionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        SpiderBuilder.WithActionResolver(resolver);
        _actionResolver = resolver;
        return this;
    }

    // ADR-0050: held here in addition to the SpiderBuilder so BuildAsync can
    // detect the misconfiguration (SemanticAct in the config + the no-op
    // default resolver) and log a warning before the crawl starts. The
    // SpiderBuilder owns the runtime wiring; this is the build-time check.
    private IActionResolver _actionResolver = NullActionResolver.Instance;

    // ADR-0058: builder-time-spawned satellite resources (CloakBrowser
    // subprocess, future Playwright IBrowser, …) register here; BuildAsync
    // hands the list to the engine, which disposes them in LIFO order
    // on engine teardown.
    private readonly List<IAsyncDisposable> _teardownHooks = new();

    /// <summary>
    /// ADR-0058: register an <see cref="IAsyncDisposable"/> the engine will
    /// dispose on teardown (LIFO order, after the adapter chain). The hook
    /// for satellite extensions that <em>spawn</em> a resource at builder
    /// time (e.g. <c>.WithCloakBrowser()</c> spawns the CloakBrowser
    /// subprocess; without this, the process would leak until the host
    /// exits). Library consumers don't typically call this directly —
    /// satellite extension methods do.
    /// </summary>
    /// <param name="disposable">The resource to dispose on engine teardown.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="disposable"/> is null.</exception>
    public ScraperEngineBuilder OnTeardown(IAsyncDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        _teardownHooks.Add(disposable);
        return this;
    }

    // Test seam (mirrors the other *ForTests internals): the number of registered
    // ADR-0058 teardown hooks, so a test can assert a satellite or transport
    // registered itself for engine disposal without launching a real subprocess.
    internal int TeardownHookCountForTests => _teardownHooks.Count;

    /// <summary>Rotate over proxies from <paramref name="source"/>, keeping
    /// only those every <paramref name="validators"/> approves.</summary>
    public ScraperEngineBuilder WithValidatedProxies(
        IProxySource source,
        IEnumerable<IProxyValidator> validators,
        ValidatedProxyProviderOptions? options = null)
    {
        SpiderBuilder.WithValidatedProxies(source, validators, options);
        return this;
    }

    /// <summary><see cref="WithValidatedProxies(IProxySource, IEnumerable{IProxyValidator}, ValidatedProxyProviderOptions)"/>
    /// with the validators as params and default options.</summary>
    public ScraperEngineBuilder WithValidatedProxies(IProxySource source, params IProxyValidator[] validators)
    {
        SpiderBuilder.WithValidatedProxies(source, validators);
        return this;
    }

    /// <summary>Append a follow step over <paramref name="linkSelector"/> (one
    /// link in the crawl's selector chain, ADR-0001).</summary>
    public ScraperEngineBuilder Follow(string linkSelector)
    {
        ConfigBuilder.Follow(linkSelector);
        return this;
    }

    /// <summary>
    /// <see cref="Follow"/> where the followed pages load via the headless
    /// browser, with optional page actions. Requires the WebReaper.Puppeteer
    /// satellite.
    /// </summary>
    public ScraperEngineBuilder FollowWithBrowser(
        string linkSelector,
        Func<PageActionBuilder,
        List<PageAction>>? actionBuilder = null)
    {
        ConfigBuilder.FollowWithBrowser(linkSelector, actionBuilder?.Invoke(new PageActionBuilder()));
        return this;
    }

    /// <summary>Append a paginate step: <paramref name="linkSelector"/> applied
    /// across pages walked via <paramref name="paginationSelector"/>
    /// (ADR-0001).</summary>
    public ScraperEngineBuilder Paginate(
        string linkSelector,
        string paginationSelector)
    {
        ConfigBuilder.Paginate(linkSelector, paginationSelector);
        return this;
    }

    /// <summary>
    /// <see cref="Paginate"/> where the pages load via the headless browser,
    /// with optional page actions. Requires the WebReaper.Puppeteer satellite.
    /// </summary>
    public ScraperEngineBuilder PaginateWithBrowser(
        string linkSelector,
        string paginationSelector,
        Func<PageActionBuilder, List<PageAction>>? actionBuilder = null)
    {
        ConfigBuilder.PaginateWithBrowser(linkSelector, paginationSelector,
            actionBuilder?.Invoke(new PageActionBuilder()));
        return this;
    }

    /// <summary>
    /// Append a Site-sweep step (ADR-0081): a recursive on-domain crawl that
    /// extracts every page AND follows its on-domain links until the frontier
    /// saturates, the whole-site sibling of <see cref="Follow"/> /
    /// <see cref="Paginate"/>, surfaced on the CLI as <c>webreaper crawl</c>.
    /// Compose it with a seed terminal:
    /// <code>
    /// var engine = await ScraperEngineBuilder
    ///     .Crawl("https://example.com")
    ///     .AsMarkdown()          // or .Extract(schema)
    ///     .Sweep()
    ///     .StopWhenAllLinksProcessed()
    ///     .WriteToConsole()
    ///     .BuildAsync();
    /// </code>
    /// <paramref name="options"/> tune the link selector, the on-domain
    /// boundary, the depth cap, and sitemap seeding (all default to the
    /// "crawl this site" behaviour). The page cap is the existing
    /// <see cref="PageCrawlLimit"/>. With <see cref="SweepOptions.Sitemap"/>
    /// on (the default), <see cref="BuildAsync"/> seeds the frontier from the
    /// Site mapper's discovered URLs too.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="SweepOptions.LinkSelector"/> is empty/whitespace
    /// (ADR-0030).</exception>
    public ScraperEngineBuilder Sweep(SweepOptions? options = null)
    {
        options ??= new SweepOptions();
        ConfigBuilder.Sweep(options);
        _sweepSitemap = options.Sitemap;
        return this;
    }

    /// <summary>
    /// Replace the <see cref="ISiteMapper"/> used for ADR-0081 sweep sitemap
    /// seeding (default <see cref="SiteMapper"/>). Internal; a test seam so
    /// the seeding path can run offline; consumers tune discovery through
    /// <see cref="SweepOptions.Sitemap"/>.
    /// </summary>
    internal ScraperEngineBuilder WithSiteMapper(ISiteMapper siteMapper)
    {
        _siteMapper = siteMapper;
        return this;
    }

    /// <summary>Use a custom <see cref="IScheduler"/> (e.g. a distributed
    /// queue) instead of the in-memory default.</summary>
    public ScraperEngineBuilder WithScheduler(IScheduler scheduler)
    {
        Scheduler = scheduler;
        return this;
    }

    /// <summary>
    /// Persist the job queue and its cursor to files so a crawl resumes across
    /// restarts (the file scheduler).
    /// </summary>
    /// <param name="fileName">Path of the job-queue file.</param>
    /// <param name="currentJobPositionFileName">Path of the cursor file
    /// tracking how far the queue has been consumed.</param>
    /// <param name="dataCleanupOnStart">Wipe the files on start vs resume;
    /// defaults to <c>false</c> (resume).</param>
    /// <exception cref="ArgumentException">either file name is
    /// null/empty/whitespace.</exception>
    public ScraperEngineBuilder WithTextFileScheduler(
        string fileName,
        string currentJobPositionFileName,
        bool dataCleanupOnStart = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentJobPositionFileName);
        Scheduler = new FileScheduler(fileName, currentJobPositionFileName, Logger, dataCleanupOnStart);
        return this;
    }

    /// <summary>Use a custom <see cref="ICookiesStorage"/> instead of the
    /// in-memory default.</summary>
    public ScraperEngineBuilder WithCookieStorage(ICookiesStorage cookiesStorage)
    {
        SpiderBuilder.WithCookieStorage(cookiesStorage);
        return this;
    }

    /// <summary>
    /// Replace the Crawl driver's retry around the per-Job Spider call
    /// (ADR-0026). The default is the internal fixed-attempts policy — four
    /// attempts total (one initial + three retries), no delay between them,
    /// every exception except <see cref="OperationCanceledException"/>
    /// triggers a retry. Supply your own <see cref="IRetryPolicy"/> for
    /// exponential backoff (e.g. wrapping a Polly resilience pipeline), to
    /// disable retries in tests, or for a satellite-aware policy. Cancellation
    /// is always cooperative — implementations must propagate
    /// <see cref="OperationCanceledException"/> without retrying.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="retryPolicy"/> is null.</exception>
    public ScraperEngineBuilder WithRetryPolicy(IRetryPolicy retryPolicy)
    {
        SpiderBuilder.WithRetryPolicy(retryPolicy);
        return this;
    }

    /// <summary>
    /// Register a custom <see cref="IBlockDetector"/> (ADR-0083) — the
    /// classifier the Spider runs on every loaded page to decide whether the
    /// load looked like a bot-check challenge. The default is the core
    /// <see cref="WebReaper.Core.Blocking.Concrete.BlockDetector"/> (HTTP status
    /// / challenge-header / body-marker heuristic). Detection is reporting, not
    /// acting: the verdict is tallied into
    /// <see cref="WebReaper.Domain.Telemetry.RunReport.BlockedPageCount"/>; the
    /// escalation / suppression decisions land in later slices. Supply your own
    /// to tune or replace the heuristic.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="detector"/> is null.</exception>
    public ScraperEngineBuilder WithBlockDetector(IBlockDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        SpiderBuilder.WithBlockDetector(detector);
        return this;
    }

    /// <summary>
    /// Register a custom <see cref="IClimbObserver"/> (ADR-0085) notified at each
    /// rung of the escalating loader's climb (attempt / blocked / climbing /
    /// succeeded / exhausted), so a consumer can stream the live climb without
    /// scraping logs. The default is the no-op <c>NullClimbObserver</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="observer"/> is null.</exception>
    public ScraperEngineBuilder WithClimbObserver(IClimbObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        SpiderBuilder.WithClimbObserver(observer);
        return this;
    }

    /// <summary>
    /// Register a custom <see cref="IPageCache"/> at the page-loader's
    /// cache-aside position (ADR-0041). The default is the no-op
    /// <c>NullPageCache</c>; the firecrawl-shaped TTL adapter is
    /// <c>InMemoryPageCache(TimeSpan maxAge)</c>, reached more directly via
    /// <see cref="WithMaxAge"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cache"/> is null.</exception>
    public ScraperEngineBuilder WithPageCache(IPageCache cache)
    {
        SpiderBuilder.WithPageCache(cache);
        return this;
    }

    /// <summary>
    /// Cache successfully-loaded pages for <paramref name="maxAge"/>
    /// (ADR-0041 — firecrawl-shaped <c>maxAge</c>). The cache is in-memory
    /// (per-process; <see cref="InMemoryPageCache"/>); a Static and a
    /// Dynamic load of the same URL are distinct entries.
    /// <see cref="TimeSpan.Zero"/> stores but never serves — a "force-fresh"
    /// crawl that still warms a snapshot for change-tracking (ADR-0048).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAge"/> is negative.</exception>
    public ScraperEngineBuilder WithMaxAge(TimeSpan maxAge)
    {
        SpiderBuilder.WithPageCache(new InMemoryPageCache(maxAge));
        return this;
    }

    /// <summary>Persist cookies to a file so an authenticated session
    /// survives restarts.</summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is
    /// null/empty/whitespace.</exception>
    public ScraperEngineBuilder WithFileCookieStorage(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        SpiderBuilder.WithFileCookieStorage(fileName);
        return this;
    }

    /// <summary>Use a custom <see cref="IScraperConfigStorage"/> (e.g. a
    /// distributed store shared with workers) instead of the in-memory
    /// default.</summary>
    public ScraperEngineBuilder WithConfigStorage(IScraperConfigStorage configStorage)
    {
        ConfigStorage = configStorage;
        return this;
    }

    /// <summary>Persist the <see cref="ScraperConfig"/> to a file.</summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is
    /// null/empty/whitespace.</exception>
    public ScraperEngineBuilder WithFileConfigStorage(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ConfigStorage = new FileScraperConfigStorage(fileName);

        return this;
    }

    /// <summary>
    /// Append a page processor to the pipeline (ADR-0038). Processors run in
    /// registration order over each crawled target page's extracted record,
    /// after the Schema fold and before the Sink fan-out — enrich, observe,
    /// filter, or repair. Implement <see cref="IPageProcessor"/> for a
    /// processor that holds state (e.g. an LLM client) or needs async warm-up.
    /// </summary>
    public ScraperEngineBuilder Process(IPageProcessor processor)
    {
        SpiderBuilder.AddProcessor(processor);
        return this;
    }

    /// <summary>
    /// Append a page processor expressed as a delegate (ADR-0038) — for a
    /// stateless stage that needs no class. Return <see cref="PageVerdict.Keep"/>
    /// to carry a record forward (enrich / observe / repair) or
    /// <see cref="PageVerdict.Drop"/> to filter the page out so no sink emits it.
    /// </summary>
    public ScraperEngineBuilder Process(
        Func<PageContext, CancellationToken, ValueTask<PageVerdict>> processor)
    {
        SpiderBuilder.AddProcessor(new DelegatePageProcessor(processor));
        return this;
    }

    /// <summary>
    /// Append a trivial synchronous enrich stage (ADR-0038): the action mutates
    /// the extracted record's JSON in place (add or change fields) and the
    /// record is kept. The dead-common page-processor case — no
    /// <see cref="IPageProcessor"/> class and no <see cref="PageVerdict"/> to
    /// learn.
    /// </summary>
    public ScraperEngineBuilder Process(Action<JsonObject> enrich)
    {
        SpiderBuilder.AddProcessor(new DelegatePageProcessor((context, _) =>
        {
            enrich(context.Data.Data);
            return ValueTask.FromResult(PageVerdict.Keep(context.Data));
        }));
        return this;
    }

    /// <summary>Degree of parallelism for the crawl loop (concurrent
    /// in-flight pages). Default 20.</summary>
    public ScraperEngineBuilder WithParallelismDegree(int parallelismDegree)
    {
        _parallelismDegree = parallelismDegree;
        return this;
    }

    /// <summary>
    /// Produce just the immutable <see cref="ScraperConfig"/> (no engine, no
    /// persistence) — the gated config terminal. The distributed start
    /// endpoint uses this, then persists the result to shared config storage
    /// for workers (ADR-0009; see <c>Examples/WebReaper.AzureFuncs</c>). Only
    /// reachable after <see cref="Crawl(string[])"/> + <see cref="ICrawlSeed.Extract"/>,
    /// so start URLs and a schema are always present (ADR-0025); the
    /// reduced-shell worker that consumes the persisted config is built with
    /// <see cref="DistributedSpiderBuilder"/>.
    /// </summary>
    public ScraperConfig Build() => ConfigBuilder.Build();

    /// <summary>
    /// Build the engine: persist the <see cref="ScraperConfig"/> to the
    /// configured <see cref="IScraperConfigStorage"/>, construct the
    /// <see cref="ISpider"/>, and return a runnable <see cref="ScraperEngine"/>
    /// (the in-process Crawl driver, ADR-0022). Start URLs and a schema are
    /// guaranteed present — this is reachable only via
    /// <see cref="Crawl(string[])"/> + <see cref="ICrawlSeed.Extract"/>
    /// (ADR-0025), so it cannot fail for an unconfigured crawl.
    /// </summary>
    public async Task<ScraperEngine> BuildAsync()
    {
        // ADR-0081: sweep sitemap seeding (default-on). Union the Site mapper's
        // discovered URLs into the start set before the config is built, so a
        // site with a sitemap but sparse internal linking is still covered. The
        // union dedups against followed links at runtime through the shared
        // Visited-link tracker. Best-effort: the mapper is itself best-effort
        // (a 404 / malformed XML yields a partial or empty list, ADR-0042).
        if (_sweepSitemap && ConfigBuilder.FirstStartUrl is { } seedUrl)
        {
            var discovered = await _siteMapper.MapAsync(seedUrl);
            ConfigBuilder.AddStartUrls(discovered);
        }

        // ADR-0034: build the immutable config, hand it to the SpiderBuilder
        // (the shell takes its Headless + ParsingScheme from it), then persist
        // it. The engine still reads ConfigStorage itself in RunAsync.
        var config = ConfigBuilder.Build();
        SpiderBuilder.WithConfig(config);

        // ADR-0050: surface the SemanticAct-without-a-real-resolver
        // misconfiguration BEFORE the crawl starts. The transport would throw
        // SemanticActResolutionException on the first dispatch anyway; this
        // makes the cause visible at build time when the config + the resolver
        // are both in hand.
        WarnIfSemanticActWithoutResolver(config);

        // ADR-0067: resolve the inference marker if set. Wrap the
        // current (or default) content extractor with
        // LearnedSchemaContentExtractor — first page pays the LLM,
        // every subsequent page runs the deterministic fold against the
        // cached schema. Throws here when the consumer chose
        // .ExtractInferred(...) but no real ISchemaInferrer is
        // registered — same pattern as AgentEngineBuilder's null-brain
        // check (ADR-0051).
        // ADR-0069: also thread the builder-registered ISchemaValidator
        // (ADR-0062) and the re-inference triggers into the wrapper.
        if (_inferenceMarker is { } marker)
        {
            if (ReferenceEquals(_schemaInferrer, NullSchemaInferrer.Instance))
            {
                throw new InvalidOperationException(
                    ".ExtractInferred(...) was called but no ISchemaInferrer was registered. " +
                    "Call .WithLlmSchemaInferrer(chatClient) on the builder before " +
                    "BuildAsync(), or supply a custom ISchemaInferrer via " +
                    ".WithSchemaInferrer(inferrer).");
            }
            // ADR-0072: read inner extractor and validator from the
            // shared pipeline instead of the previously-duplicated
            // ScraperEngineBuilder._schemaValidator field.
            var inner = _pipeline.GetExtractorOrDefault();
            var validator = _pipeline.Validator ?? SchemaSatisfiedValidator.Instance;
            var wrapped = new LearnedSchemaContentExtractor(
                _schemaInferrer, inner, marker.Goal, Logger,
                validator,
                _reInferAfterFailures,
                _maxReInferencesPerInstance);
            _pipeline.WithContentExtractor(wrapped);
            // ADR-0058: register the wrapper as a teardown hook so the
            // SemaphoreSlim disposes on engine teardown.
            OnTeardown(wrapped);
        }

        // ADR-0072: sync the pipeline's resolved extractor into the
        // SpiderBuilder once, immediately before SpiderBuilder.Build —
        // the spider doesn't read from the pipeline directly; it gets
        // the finalized extractor via WithContentExtractor.
        SpiderBuilder.WithContentExtractor(_pipeline.GetExtractorOrDefault());
        var spider = SpiderBuilder.Build();
        await ConfigStorage.CreateConfigAsync(config);

        return new ScraperEngine(
            _parallelismDegree, ConfigStorage, Scheduler, spider,
            SpiderBuilder.DriverLinkTracker, SpiderBuilder.DriverSinks, Logger,
            SpiderBuilder.DriverPageProcessors,
            retryPolicy: SpiderBuilder.DriverRetryPolicy,
            // ADR-0058: hand the LIFO list of builder-registered teardown
            // hooks to the engine; await using on the engine fires them on
            // scope exit. List shared by reference so a same-builder build
            // is rare (and would still see the same hooks).
            ownedDisposables: _teardownHooks,
            // ADR-0066: hand the satellite-registered telemetry hooks
            // through; null when no LLM adapter ran. RunAsync returns
            // RunReport(Llm: opaque snapshot, Duration: wall-clock).
            telemetryHooks: TelemetryHooks);
    }

    // ADR-0050: the SemanticAct-without-a-resolver detector. Reads both
    // potential homes (start-page actions on the ScraperConfig + per-step
    // actions on every LinkPathSelector) for any SemanticAct arm. Logs at
    // Warning when one is found AND the resolver is still the default
    // NullActionResolver — no exception, the throw happens at dispatch.
    private void WarnIfSemanticActWithoutResolver(ScraperConfig config)
    {
        if (!ReferenceEquals(_actionResolver, NullActionResolver.Instance)) return;
        if (!HasSemanticAct(config)) return;
        Logger.LogWarning(
            "Crawl config contains a PageAction.SemanticAct but no IActionResolver " +
            "is registered. The SemanticAct dispatch will throw " +
            "SemanticActResolutionException at the first dynamic page. Register a " +
            "resolver via ScraperEngineBuilder.WithActionResolver(...) — or, for " +
            "the LLM-backed default, add the WebReaper.AI satellite and call " +
            ".WithLlmActionResolver(chatClient).");
    }

    private static bool HasSemanticAct(ScraperConfig config)
    {
        if (config.PageActions is { } start && start.Any(a => a is PageAction.SemanticAct))
            return true;
        foreach (var selector in config.LinkPathSelectors)
        {
            if (selector.PageActions is { } actions && actions.Any(a => a is PageAction.SemanticAct))
                return true;
        }
        return false;
    }
}
