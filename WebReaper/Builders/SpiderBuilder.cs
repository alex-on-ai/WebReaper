using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using WebReaper.Core.Actions.Abstract;
using WebReaper.Core.Actions.Concrete;
using WebReaper.Core.Blocking.Abstract;
using WebReaper.Core.CookieStorage.Abstract;
using WebReaper.Core.CookieStorage.Concrete;
using WebReaper.Core.LinkTracker.Abstract;
using WebReaper.Core.LinkTracker.Concrete;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Core.Loaders.Concrete;
using WebReaper.Core.Parser.Abstract;
using WebReaper.Core.Parser.Concrete;
using WebReaper.Core.Crawling.Concrete;
using WebReaper.Core.Spider.Abstract;
using WebReaper.Core.Spider.Concrete;
using WebReaper.Domain;
using WebReaper.Domain.Selectors;
using WebReaper.Infra.Abstract;
using WebReaper.Infra.Concrete;
using WebReaper.Processing.Abstract;
using WebReaper.Proxy;
using WebReaper.Proxy.Abstract;
using WebReaper.Proxy.Concrete;
using WebReaper.Sinks.Abstract;
using WebReaper.Sinks.Concrete;
using WebReaper.Sinks.Models;

namespace WebReaper.Builders;

/// <summary>
/// Internal collaborator of <see cref="ScraperEngineBuilder"/> (ADR-0009):
/// the runtime-component wiring (loaders, parsers, sinks, trackers, cookie
/// storage). Not public — the registration seam lives only on
/// <see cref="ScraperEngineBuilder"/>; a bare <see cref="Core.Spider.Abstract.ISpider"/>
/// is obtained via <see cref="DistributedSpiderBuilder.BuildSpider"/> (the
/// ADR-0009 reduced shell) or constructed by the engine path internally.
/// </summary>
internal class SpiderBuilder
{
    private List<IPageProcessor> PageProcessors { get; } = new();

    private List<IScraperSink> Sinks { get; } = new();

    private ILogger Logger { get; set; } = NullLogger.Instance;

    // Nullable until ScraperEngineBuilder.BuildAsync calls WithConfig
    // (ADR-0034: required parameter on the Build call). The two read
    // sites in Build() are post-WithConfig and use the null-forgiving
    // operator to surface the structural invariant.
    private ScraperConfig? Config { get; set; }

    private IVisitedLinkTracker SiteLinkTracker { get; set; } = new InMemoryVisitedLinkTracker();

    private IContentExtractor? ContentExtractor { get; set; }

    private IPageLoader? PageLoader { get; set; }

    // ADR-0041: cache-aside collaborator on PageLoader. NullPageCache is
    // the default (preserves pre-0041 behaviour); WithPageCache /
    // WithMaxAge wires a real cache.
    private IPageCache PageCache { get; set; } = new NullPageCache();

    // ADR-0083 slice 5: the ordered Dynamic (browser-class) rungs of the
    // EscalatingPageLoader ladder, lowest first. Each WithLoadTransport call
    // appends one rung, so a consumer (e.g. the CLI) registers a vanilla browser
    // rung and then a stealth rung to build HTTP -> browser -> stealth. Empty =>
    // HTTP-only, and a Dynamic load hits the BrowserNotConfigured sentinel.
    private readonly List<Func<ICookiesStorage, IProxyProvider?, ILogger, IActionResolver, IPageLoadTransport>> _dynamicTransportFactories = new();

    // ADR-0050: the per-Spider IActionResolver collaborator that resolves
    // SemanticAct(intent) arms to concrete PageActions. NullActionResolver is
    // the default — dispatching SemanticAct with it registered throws at the
    // transport, and a warning fires at engine construction if the config
    // contains any SemanticAct.
    private IActionResolver ActionResolver { get; set; } = NullActionResolver.Instance;

    private IProxyProvider? ProxyProvider { get; set; }

    private CookieContainer Cookies { get; } = new();

    private ICookiesStorage CookieStorage { get; set; } = new InMemoryCookieStorage();

    // ADR-0026: the Crawl driver's retry around the per-Job Spider call is a
    // named seam. Default is the internal fixed-attempts adapter (one initial
    // + three retries, the pre-0026 behaviour). Replaced via
    // ScraperEngineBuilder.WithRetryPolicy.
    private IRetryPolicy RetryPolicy { get; set; } = new FixedAttemptsRetryPolicy();

    // ADR-0083: the block detector the Spider runs on every loaded page. The
    // default core BlockDetector ("am I being blocked?" is a core scraping
    // concern); replaced via ScraperEngineBuilder.WithBlockDetector. Type
    // fully-qualified so the field initializer is unambiguous against the
    // same-named property.
    private IBlockDetector BlockDetector { get; set; } =
        new Core.Blocking.Concrete.BlockDetector();

    // ADR-0085: the climb-progress observer the EscalatingPageLoader notifies per
    // rung; default no-op. Replaced via ScraperEngineBuilder.WithClimbObserver.
    private IClimbObserver ClimbObserver { get; set; } = NullClimbObserver.Instance;

    public SpiderBuilder WithContentExtractor(IContentExtractor extractor)
    {
        ContentExtractor = extractor;
        return this;
    }

    /// <summary>
    /// Parse responses as JSON instead of HTML (issue #27). Schema
    /// selectors become JSONPath expressions. For a different document
    /// shape (e.g. HtmlAgilityPack, XPath), implement
    /// <see cref="Core.Parser.Abstract.ISchemaBackend{TNode}"/> and pass
    /// <c>new SchemaFold&lt;TNode&gt;(backend, logger)</c> to
    /// <see cref="WithContentExtractor"/> — it reuses the shared Schema fold
    /// rather than re-implementing the walk.
    /// </summary>
    public SpiderBuilder WithJsonContentParser()
    {
        ContentExtractor = new SchemaFold<JsonNode>(new JsonSchemaBackend(), Logger);
        return this;
    }

    /// <summary>
    /// Parse responses as HTML but with XPath 1.0 selectors instead of CSS
    /// (discussion #17). Schema selectors become XPath expressions over the
    /// same AngleSharp DOM the default CSS parser uses; <c>IsList</c>,
    /// attributes and type coercion behave identically. Like
    /// <see cref="WithJsonContentParser"/> this is just a different
    /// <see cref="Core.Parser.Abstract.ISchemaBackend{TNode}"/> behind the
    /// one shared Schema fold (ADR 0002 / 0007).
    /// </summary>
    public SpiderBuilder WithXPathContentParser()
    {
        ContentExtractor = new SchemaFold<AngleSharp.Dom.IParentNode>(
            new AngleSharpXPathSchemaBackend(), Logger);
        return this;
    }

    public SpiderBuilder WithLogger(ILogger logger)
    {
        Logger = logger;
        return this;
    }

    public SpiderBuilder WithLinkTracker(IVisitedLinkTracker linkTracker)
    {
        SiteLinkTracker = linkTracker;
        return this;
    }

    // ADR-0034: the immutable ScraperConfig the per-Job Spider shell needs
    // (its Headless flag and ParsingScheme). Supplied by whichever Crawl
    // driver builds the Spider — ScraperEngineBuilder has the object in hand,
    // the distributed worker fetches it. The shell never reads config storage.
    public SpiderBuilder WithConfig(ScraperConfig config)
    {
        Config = config;
        return this;
    }

    public SpiderBuilder SetCookies(Action<CookieContainer> setCookies)
    {
        setCookies(Cookies);
        return this;
    }

    public SpiderBuilder AddSink(IScraperSink sink)
    {
        Sinks.Add(sink);
        return this;
    }

    public SpiderBuilder WriteToConsole()
    {
        return AddSink(new ConsoleSink());
    }

    public SpiderBuilder AddProcessor(IPageProcessor processor)
    {
        PageProcessors.Add(processor);
        return this;
    }

    public SpiderBuilder WriteToJsonFile(string filePath, bool dataCleanupOnStart)
    {
        return AddSink(new JsonLinesFileSink(filePath, dataCleanupOnStart));
    }

    /// <summary>
    /// Supply a custom page loader. By default <see cref="Build"/> wires the
    /// HTTP and headless-browser transports behind one <see cref="PageLoader"/>
    /// and passes the optional proxy provider to both (ADR 0004).
    /// </summary>
    public SpiderBuilder WithPageLoader(IPageLoader pageLoader)
    {
        PageLoader = pageLoader;
        return this;
    }

    /// <summary>
    /// Register a transport for Dynamic (headless-browser) pages (ADR-0009).
    /// The factory is invoked at <see cref="Build"/> time with the builder's
    /// resolved cookie storage, optional proxy provider and logger — the same
    /// collaborators the HTTP transport gets — so a satellite (e.g.
    /// WebReaper.Cdp's <c>.WithCdpPageLoader()</c>) needs no arguments and the
    /// pre-7.0 default behaviour (one shared cookie container, issue #26) is
    /// preserved. With no registration the core default is HTTP-only and a
    /// Dynamic load throws an actionable message
    /// (<see cref="BrowserNotConfiguredPageLoadTransport"/>).
    /// <para>
    /// ADR-0083: each call <b>appends</b> a rung to the escalating loader's
    /// ladder, in registration order above the HTTP rung. One call is the common
    /// case (a single browser tier); calling it twice (a vanilla browser rung
    /// then a stealth rung) builds the HTTP -> browser -> stealth climb the CLI
    /// flag wiring uses.
    /// </para>
    /// </summary>
    public SpiderBuilder WithLoadTransport(
        Func<ICookiesStorage, IProxyProvider?, ILogger, IActionResolver, IPageLoadTransport> dynamicTransportFactory)
    {
        ArgumentNullException.ThrowIfNull(dynamicTransportFactory);
        _dynamicTransportFactories.Add(dynamicTransportFactory);
        return this;
    }

    /// <summary>Register the <see cref="IActionResolver"/> the Puppeteer
    /// transport invokes for <see cref="WebReaper.Domain.PageActions.PageAction.SemanticAct"/>
    /// arms (ADR-0050). The default is <see cref="NullActionResolver"/>; the
    /// LLM-backed implementation ships in the <c>WebReaper.AI</c> satellite.</summary>
    public SpiderBuilder WithActionResolver(IActionResolver actionResolver)
    {
        ArgumentNullException.ThrowIfNull(actionResolver);
        ActionResolver = actionResolver;
        return this;
    }

    public SpiderBuilder WithProxies(IProxyProvider proxyProvider)
    {
        ProxyProvider = proxyProvider;
        return this;
    }

    /// <summary>
    /// Use proxies from <paramref name="source"/>, but only after they
    /// pass every supplied validator. Plugs into the same pipeline as
    /// <see cref="WithProxies(IProxyProvider)"/>.
    /// </summary>
    public SpiderBuilder WithValidatedProxies(
        IProxySource source,
        IEnumerable<IProxyValidator> validators,
        ValidatedProxyProviderOptions? options = null)
    {
        ProxyProvider = new ValidatedProxyProvider(source, validators, options, Logger);
        return this;
    }

    public SpiderBuilder WithValidatedProxies(IProxySource source, params IProxyValidator[] validators)
        => WithValidatedProxies(source, (IEnumerable<IProxyValidator>)validators);

    public SpiderBuilder WriteToCsvFile(string filePath, bool dataCleanupOnStart)
    {
        return AddSink(new CsvFileSink(filePath, dataCleanupOnStart));
    }

    public SpiderBuilder WithFileCookieStorage(string fileName)
    {
        CookieStorage = new FileCookieStorage(fileName, Logger);
        return this;
    }

    public SpiderBuilder WithCookieStorage(ICookiesStorage cookiesStorage)
    {
        CookieStorage = cookiesStorage;
        return this;
    }

    public SpiderBuilder WithRetryPolicy(IRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        RetryPolicy = retryPolicy;
        return this;
    }

    /// <summary>Register a custom <see cref="IBlockDetector"/> (ADR-0083); the
    /// default is the core <see cref="Core.Blocking.Concrete.BlockDetector"/>.</summary>
    public SpiderBuilder WithBlockDetector(IBlockDetector blockDetector)
    {
        ArgumentNullException.ThrowIfNull(blockDetector);
        BlockDetector = blockDetector;
        return this;
    }

    /// <summary>Register a custom <see cref="IClimbObserver"/> (ADR-0085); the
    /// default is the no-op <see cref="NullClimbObserver"/>.</summary>
    public SpiderBuilder WithClimbObserver(IClimbObserver climbObserver)
    {
        ArgumentNullException.ThrowIfNull(climbObserver);
        ClimbObserver = climbObserver;
        return this;
    }

    /// <summary>Register a custom <see cref="IPageCache"/> (ADR-0041);
    /// the default is <see cref="NullPageCache"/> (no cache).</summary>
    public SpiderBuilder WithPageCache(IPageCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        PageCache = cache;
        return this;
    }

    // ADR-0046: read the current extractor (or build the default
    // SchemaFold over the AngleSharp/CSS backend) so ScraperEngineBuilder
    // .WithFallbackExtractor can compose it into an ExtractionRouter.
    // Idempotent and does NOT mutate ContentExtractor — the caller is
    // expected to replace it via WithContentExtractor.
    internal IContentExtractor GetContentExtractorOrDefault(ILogger logger)
        => ContentExtractor ?? new SchemaFold<AngleSharp.Dom.IParentNode>(
            new AngleSharpSchemaBackend(), logger);

    // ADR-0022: the Crawl driver (ScraperEngine) owns these now — the reduced
    // Spider shell no longer fans out to Sinks, tracks links, or runs the
    // page-processor pipeline. ScraperEngineBuilder reads them to construct
    // the driver.
    internal List<IScraperSink> DriverSinks => Sinks;
    internal IVisitedLinkTracker DriverLinkTracker => SiteLinkTracker;
    // ADR-0038: the ordered page-processor pipeline.
    internal IReadOnlyList<IPageProcessor> DriverPageProcessors => PageProcessors;
    // ADR-0026.
    internal IRetryPolicy DriverRetryPolicy => RetryPolicy;

    public ISpider Build()
    {
        // Default extractor: the deterministic Schema fold over the CSS/HTML
        // backend. ADR-0039 — no named shell; the builder constructs the fold.
        ContentExtractor ??= new SchemaFold<AngleSharp.Dom.IParentNode>(
            new AngleSharpSchemaBackend(), Logger);

        // One loader (ADR 0004), the block-aware climbing loader (ADR-0083). The
        // HTTP transport is the core default Static rung; the Dynamic rungs are
        // the registered transport factories (e.g. WebReaper.Cdp's
        // .WithCdpPageLoader(), appended in order), or, with none, the actionable
        // BrowserNotConfigured sentinel — core is HTTP-only by default (ADR-0009).
        // The proxy/no-proxy choice is still a (possibly null) provider handed in
        // to whichever transports exist, not a branch. Rungs run lowest→highest:
        // HTTP (Static) then each Dynamic rung (browser, then stealth when the CLI
        // flag wiring registered it); a Static page climbs from HTTP, a Dynamic
        // page starts at the first Dynamic rung.
        // ADR-0041: PageCache is the cache-aside collaborator (NullPageCache by
        // default); the loader never caches a blocked result, and a climb
        // bypasses the cache for the higher rung.
        // The transports are built only when no custom loader was supplied (a
        // WithPageLoader wiring must not invoke the dynamic transport factories —
        // they may launch a browser), preserving the old `??=` laziness.
        if (PageLoader is null)
        {
            var tiers = new List<PageLoadTier>
            {
                new(PageType.Static, new HttpPageLoadTransport(CookieStorage, ProxyProvider, Logger)),
            };
            if (_dynamicTransportFactories.Count > 0)
                foreach (var factory in _dynamicTransportFactories)
                    tiers.Add(new PageLoadTier(
                        PageType.Dynamic, factory(CookieStorage, ProxyProvider, Logger, ActionResolver)));
            else
                tiers.Add(new PageLoadTier(PageType.Dynamic, new BrowserNotConfiguredPageLoadTransport()));

            PageLoader = new EscalatingPageLoader(
                tiers,
                BlockDetector,
                new HostTierFloor(),
                Logger,
                PageCache,
                ClimbObserver);
        }

        CookieStorage.AddAsync(Cookies);

        // ADR-0081: derive the Sweep on-domain + depth policy from the config
        // (anchor host from the start URL) and thread it into the step. Covers
        // both Crawl drivers: the engine path and the distributed worker both
        // build through here. Null on a non-sweep crawl, so the recursive
        // branch never fires.
        var crawlStep = new CrawlStep(ContentExtractor, BuildSweepPolicy(Config!));

        // Config is null until WithConfig is called; ADR-0034 makes it
        // required by the time Build runs (the outer ScraperEngineBuilder
        // .BuildAsync calls WithConfig before Build). Null-forgive on
        // the read sites — the runtime invariant holds.
        var spider = new Spider(
            crawlStep,
            PageLoader,
            BlockDetector,
            Config!.Headless,
            Config.ParsingScheme);

        return spider;
    }

    // ADR-0081: the Sweep policy is built only when the chain carries a
    // recursive selector. The anchor host is the start URL's host, fixed for
    // the crawl, so the on-domain boundary stays correct as pages are reached
    // through www / subdomains (a per-page host would break --include-subdomains).
    // No parseable start host ⇒ null, and the step falls back to each page's
    // own host.
    private static SweepPolicy? BuildSweepPolicy(ScraperConfig config)
    {
        if (!config.LinkPathSelectors.Any(s => s.Recursive)) return null;

        var anchorHost = config.StartUrls
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
            .FirstOrDefault(h => !string.IsNullOrEmpty(h));

        return anchorHost is null
            ? null
            : new SweepPolicy(anchorHost, config.IncludeSubdomains, config.MaxDepth);
    }
}
