using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using WebReaper.AI;
using WebReaper.AI.Http;
using WebReaper.Builders;
using WebReaper.Core.Mapping;
using WebReaper.Domain.Parsing;
using WebReaper.Sinks.Models;

namespace WebReaper.Mcp;

// ADR-0049: the MCP tools the satellite exposes (scrape, map, extract, and
// extract_with_prompt). Each wraps an existing library API; the tool layer is
// thin glue between the MCP-protocol JSON shape and the library's fluent
// builders. ADR-0084 added extract_with_prompt (schema-free LLM extraction).

[McpServerToolType]
public static class WebReaperTools
{
    [McpServerTool(Name = "scrape"), Description(
        "Fetch a URL and return its main content as LLM-ready Markdown. " +
        "The lowest-cost call against any site. Useful for reading a page " +
        "into context.")]
    public static async Task<string> Scrape(
        [Description("The URL to scrape.")] string url,
        [Description("Use the headless browser (for JS-rendered pages). Auto-spawns a system Chrome / Chromium / Edge via WebReaper.Cdp; install a Chromium-family browser on the MCP host first. Default false.")] bool browser = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));

        var seed = browser
            ? ScraperEngineBuilder.CrawlWithBrowser(url)
            : ScraperEngineBuilder.Crawl(url);

        var records = new List<ParsedData>();
        var builder = seed.AsMarkdown()
            .Subscribe(records.Add)
            .StopWhenAllLinksProcessed();

        // ADR-0073 / ADR-0086: browser=true launches managed Chromium, or
        // connects to WEBREAPER_CDP_URL (a shared browser sidecar) when set.
        await RunBrowserAwareAsync(builder, browser);

        var output = new StringBuilder();
        foreach (var record in records)
        {
            var title = record.Data["title"]?.GetValue<string>() ?? string.Empty;
            var md = record.Data["markdown"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrEmpty(title))
                output.Append("# ").Append(title).Append("\n\n");
            output.AppendLine(md);
        }
        return output.ToString().TrimEnd();
    }

    [McpServerTool(Name = "map"), Description(
        "Discover URLs on a site via sitemap.xml + root-page link " +
        "extraction. Returns a newline-separated list of URLs.")]
    public static async Task<string> Map(
        [Description("The site root URL.")] string url,
        [Description("Optional case-insensitive substring filter on the returned URLs (e.g. \"/blog/\").")] string? search = null,
        [Description("Cap on the number of URLs to return. Default 1000.")] int maxUrls = 1000,
        [Description("Keep off-site URLs in the result. Default false.")] bool allowOffsite = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));

        var options = new MapOptions(
            MaxUrls: maxUrls,
            AllowOffsite: allowOffsite,
            Search: search);

        var urls = await ScraperEngineBuilder.MapAsync(url, options);
        return string.Join("\n", urls);
    }

    [McpServerTool(Name = "extract"), Description(
        "Extract structured fields from a URL using a JSON schema. The " +
        "schema mirrors the WebReaper Schema shape: " +
        "{ field, children: [ { field, selector, type, is_list }, ... ] }. " +
        "Returns the extracted record(s) as JSON Lines.")]
    public static async Task<string> Extract(
        [Description("The URL to extract from.")] string url,
        [Description("The extraction schema as JSON. See the WebReaper docs for the shape.")] string schemaJson,
        [Description("Use the headless browser. Auto-spawns a system Chrome / Chromium / Edge via WebReaper.Cdp; install a Chromium-family browser on the MCP host first. Default false.")] bool browser = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(schemaJson))
            throw new ArgumentException("Schema JSON is required.", nameof(schemaJson));

        Schema schema;
        try
        {
            schema = ParseSchema(schemaJson);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Schema JSON is invalid: {ex.Message}", nameof(schemaJson));
        }

        var seed = browser
            ? ScraperEngineBuilder.CrawlWithBrowser(url)
            : ScraperEngineBuilder.Crawl(url);

        var records = new List<ParsedData>();
        var builder = seed.Extract(schema)
            .Subscribe(records.Add)
            .StopWhenAllLinksProcessed();

        // ADR-0086: see Scrape() for the browser-wiring rationale.
        await RunBrowserAwareAsync(builder, browser);

        // JSON Lines: one record per line.
        return string.Join("\n", records.Select(r => r.Data.ToJsonString()));
    }

    [McpServerTool(Name = "extract_with_prompt"), Description(
        "Extract structured data from a URL with an LLM, using a natural-language " +
        "instruction instead of a CSS schema (e.g. \"each person's name, title, and email\"). " +
        "Returns the extracted record(s) as JSON Lines. Requires an OpenAI-compatible LLM " +
        "endpoint configured on the MCP host: set WEBREAPER_LLM_MODEL and WEBREAPER_LLM_BASE_URL " +
        "(e.g. https://api.openai.com/v1 or http://localhost:11434/v1), with the API key in " +
        "WEBREAPER_LLM_API_KEY (or OPENAI_API_KEY). Costs one LLM call.")]
    public static async Task<string> ExtractWithPrompt(
        [Description("The URL to extract from.")] string url,
        [Description("Natural-language description of the data to extract.")] string prompt,
        [Description("Use the headless browser (for JS-rendered pages). Auto-spawns a system Chrome / Chromium / Edge via WebReaper.Cdp. Default false.")] bool browser = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));

        using var chatClient = CreateChatClient();

        var seed = browser
            ? ScraperEngineBuilder.CrawlWithBrowser(url)
            : ScraperEngineBuilder.Crawl(url);

        var records = new List<ParsedData>();
        // ADR-0084: the schema-free LLM strategy. The chat client is disposed
        // on method exit, after the engine (declared later, so disposed first).
        var builder = seed.ExtractWithPrompt(chatClient, prompt)
            .Subscribe(records.Add)
            .StopWhenAllLinksProcessed();

        // ADR-0086: see Scrape() for the browser-wiring rationale.
        await RunBrowserAwareAsync(builder, browser);

        return string.Join("\n", records.Select(r => r.Data.ToJsonString()));
    }

    [McpServerTool(Name = "crawl"), Description(
        "Crawl a whole site: recursively follow on-domain links from the start URL "
        + "and return one Markdown record per page as JSON Lines. WARNING: this is a "
        + "single long BLOCKING call with NO progress feedback, bounded by max_pages "
        + "(default 50, hard cap 1000). For a large site prefer 'map' to list URLs, "
        + "then 'scrape' each URL, so every call stays short and you keep per-URL control.")]
    public static async Task<string> Crawl(
        [Description("The site root URL to crawl.")] string url,
        [Description("Maximum pages to sweep. Default 50, hard cap 1000.")] int maxPages = CrawlBounds.DefaultMaxPages,
        [Description("Use the headless browser for each page (JS-rendered sites). Default false.")] bool browser = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));

        var cap = CrawlBounds.Validate(maxPages);

        var seed = browser
            ? ScraperEngineBuilder.CrawlWithBrowser(url)
            : ScraperEngineBuilder.Crawl(url);

        // ADR-0081 site sweep, bounded. The crawl loop is parallel (ADR-0022),
        // so sink emits are concurrent; guard the collection.
        var records = new List<ParsedData>();
        var builder = seed.AsMarkdown()
            .Sweep(new SweepOptions())
            .PageCrawlLimit(cap)
            .Subscribe(r => { lock (records) records.Add(r); })
            .StopWhenAllLinksProcessed();

        await RunBrowserAwareAsync(builder, browser);

        // JSON Lines: one record per swept page.
        return string.Join("\n", records.Select(r => r.Data.ToJsonString()));
    }

    // ADR-0086: resolve the browser plan (launch vs connect-to-CDP-url), apply
    // it, and run the engine. Managed-Chromium launches pass through a
    // concurrency gate; connect / HTTP calls do not. `await using` tears the
    // engine (and any spawned Chromium) down on completion (ADR-0058).
    private static async Task RunBrowserAwareAsync(ScraperEngineBuilder builder, bool browser)
    {
        var plan = BrowserTransport.Select(
            browser, Environment.GetEnvironmentVariable(BrowserTransport.CdpUrlEnvVar));
        builder = builder.ApplyBrowser(plan);

        var gated = plan.Mode == BrowserLaunchMode.Launch;
        if (gated) await BrowserLaunchGate.WaitAsync();
        try
        {
            await using var engine = await builder.BuildAsync();
            await engine.RunAsync();
        }
        finally
        {
            if (gated) BrowserLaunchGate.Release();
        }
    }

    // ADR-0084: build the OpenAI-compatible chat client from environment config.
    // The same explicit-config contract as the CLI: model + base URL are
    // required; the API key is read from the environment only, never a param.
    internal static OpenAiCompatibleChatClient CreateChatClient()
    {
        var model = Environment.GetEnvironmentVariable("WEBREAPER_LLM_MODEL");
        var baseUrl = Environment.GetEnvironmentVariable("WEBREAPER_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("WEBREAPER_LLM_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "extract_with_prompt needs an LLM model: set the WEBREAPER_LLM_MODEL environment variable.");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "extract_with_prompt needs an LLM endpoint: set WEBREAPER_LLM_BASE_URL "
                + "(e.g. https://api.openai.com/v1).");

        return new OpenAiCompatibleChatClient(baseUrl, model, apiKey);
    }

    // Tiny JSON → Schema parser (same shape the CLI accepts, ADR-0043).
    // Kept private to the satellite; the CLI has its own copy because
    // satellite ↔ CLI sharing would require a third package for one
    // <100-line function.
    private static Schema ParseSchema(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("Schema must be a JSON object at the root.");
        return BuildSchema(node);
    }

    private static Schema BuildSchema(JsonObject obj)
    {
        var children = obj["children"] as JsonArray;
        if (children is null || children.Count == 0)
        {
            return WrapAsSchema(BuildElement(obj));
        }
        var field = obj["field"]?.GetValue<string>();
        var selector = obj["selector"]?.GetValue<string>();
        var isList = obj["is_list"]?.GetValue<bool>() ?? false;
        var container = field is not null
            ? new Schema(field) { Selector = selector ?? string.Empty, IsList = isList }
            : new Schema();
        foreach (var child in children)
            if (child is JsonObject co) container.Add(BuildElement(co));
        return container;
    }

    private static SchemaElement BuildElement(JsonObject obj)
    {
        var field = obj["field"]?.GetValue<string>()
            ?? throw new ArgumentException("Schema element is missing 'field'.");
        var children = obj["children"] as JsonArray;
        if (children is not null && children.Count > 0)
            return BuildSchema(obj);
        var selector = obj["selector"]?.GetValue<string>() ?? string.Empty;
        var attr = obj["attr"]?.GetValue<string>();
        var isList = obj["is_list"]?.GetValue<bool>() ?? false;
        var element = new SchemaElement(field, selector)
        {
            Type = ParseDataType(obj["type"]?.GetValue<string>()),
            IsList = isList
        };
        if (attr is not null) element.Attr = attr;
        return element;
    }

    private static Schema WrapAsSchema(SchemaElement el) =>
        el is Schema s ? s : new Schema { el };

    private static DataType? ParseDataType(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" => null,
        "string" => DataType.String,
        "integer" or "int" => DataType.Integer,
        "float" or "double" or "decimal" => DataType.Float,
        "boolean" or "bool" => DataType.Boolean,
        "datetime" or "date" => DataType.DataTime,
        _ => throw new ArgumentException(
            $"Unknown schema type '{raw}'.")
    };
}
