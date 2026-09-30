using System.Diagnostics;
using WebReaper.Builders;
using WebReaper.Cdp;
using WebReaper.Cli.Stealth;
using WebReaper.Sinks.Models;

namespace WebReaper.Cli.Commands;

// `webreaper scrape <url>` — the funnel's primitive call. Markdown to stdout by
// default; JSON when --schema is supplied.
//
// ADR-0083: the escalating page loader does the climbing now. This command's job
// is to assemble the rungs and pick where the start page enters:
//   • no transport flag → start at HTTP, auto-climb to the browser rung on a block
//   • --browser (or --browser-cdp-url) → start at the browser rung
//   • --stealth → start at the stealth rung (the top)
// Stealth-rung inclusion is decided once, at startup (ADR-0056 policy:
// --stealth / --auto-stealth / WEBREAPER_AUTO_STEALTH / interactive Y/n;
// --no-auto-stealth caps at the browser rung). The single run then climbs
// autonomously — there is no post-hoc detect/retry loop (the dead ADR-0056
// BotCheckDetector escalation is gone). The block detector's per-run tally on
// the RunReport drives the exit code.
internal static class ScrapeCommand
{
    private static readonly string[] ChromeNames =
        ["google-chrome", "chromium", "chrome", "microsoft-edge", "msedge"];

    // The stealth rung's backend (ADR-0055 registry row): installed by name,
    // launched with its vendor flags.
    private static readonly StealthBackend StealthBackend = KnownStealthBackends.Find("cloakbrowser")!;

    public static async Task<int> RunAsync(ParsedArgs args)
    {
        if (args.Positional.Count < 1)
            throw new CliException("Missing <url>. Usage: webreaper scrape <url> [flags]");

        var ctx = ParseContext(args);

        var startTier = EscalationPlan.ResolveStartTier(ctx.Browser, ctx.Stealth);

        // Decide stealth-rung inclusion once, before building the engine.
        var includeStealth = await ResolveStealthAsync(ctx);

        // If the stealth rung is in, install CloakBrowser now so the rung's lazy
        // launch finds the binary mid-climb. A failed install degrades to the
        // browser rung rather than aborting.
        string? stealthPath = null;
        if (includeStealth)
        {
            stealthPath = await EnsureStealthInstalledAsync();
            if (stealthPath is null)
            {
                Console.Error.WriteLine("⚠  Stealth unavailable; falling back to the browser tier.");
                includeStealth = false;
                if (startTier == StartTier.Stealth) startTier = StartTier.Browser;
            }
        }

        var result = await RunOnceAsync(ctx, startTier, includeStealth, stealthPath);
        WriteEmptyHint(ctx, result.Records.Count);
        return await ShipAsync(ctx, result);
    }

    // ----- stealth-inclusion decision (startup) -----

    // Resolve the flag-driven decision; for the interactive AskUser case, prompt
    // once. Non-interactive AskUser defaults to No (never download hundreds of
    // MB speculatively when there is no one to ask).
    private static async Task<bool> ResolveStealthAsync(ScrapeContext ctx)
    {
        switch (EscalationPlan.ResolveStealth(ctx.Browser, ctx.Stealth, ctx.AutoStealth, ctx.NoAutoStealth))
        {
            case StealthInclusion.Included:
                return true;
            case StealthInclusion.Excluded:
                return false;
            default:
                if (Console.IsInputRedirected) return false;
                var download = StealthBackend.BuildFor(StealthInstaller.CurrentPlatform()) is { } build
                    ? $"downloads {StealthBackend.DisplayName} ~{build.SizeMb} MB unless already installed"
                    : StealthBackend.DisplayName;
                Console.Error.Write($"?  Enable the stealth fallback tier ({download})? [y/N] ");
                var reply = Console.ReadLine()?.Trim();
                return await Task.FromResult(
                    reply is not null && reply.Equals("y", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Install CloakBrowser via `webreaper stealth install cloakbrowser --yes` and
    // return its resolved binary path, or null if install / resolution failed.
    // Substitutability (ADR-0056): the same commands the user could run by hand.
    private static async Task<string?> EnsureStealthInstalledAsync()
    {
        Console.Error.WriteLine(
            "↓  Ensuring CloakBrowser via `webreaper stealth install cloakbrowser --yes`...");
        var code = await RunSelfAsync("stealth", "install", StealthBackend.Name, "--yes");
        if (code != 0)
        {
            Console.Error.WriteLine($"✗  Stealth install failed (exit {code}).");
            return null;
        }
        return await CaptureSelfAsync("stealth", "path", StealthBackend.Name);
    }

    // ----- ship the records, block-aware exit code (ADR-0083) -----

    // Write the run's records to the configured output, then return the exit
    // code: non-zero when the core block detector flagged (and the driver
    // suppressed) any page this run, zero otherwise — so an unattended caller can
    // detect a blocked scrape.
    private static async Task<int> ShipAsync(ScrapeContext ctx, AttemptResult attempt)
    {
        // Markdown mode (no schema, no AI) writes .md files under --output-dir;
        // schema / prompt / infer write .json.
        var asMarkdown = ctx.SchemaPath is null && !ctx.Ai.Any;
        await RecordOutput.EmitAsync(attempt.Records, ctx.Output, ctx.OutputDir, ctx.Open, asMarkdown);
        if (attempt.BlockedPageCount > 0)
        {
            Console.Error.WriteLine(
                $"⚠  {attempt.BlockedPageCount} page(s) still blocked at the top tier; "
                + "the site may need a stronger transport or a captcha solver.");
            return 1;
        }
        return 0;
    }

    // ----- empty-result hint -----

    // When a scrape returns no records, point the user at the next transport to
    // try instead of silently shipping empty stdout. The decision is the pure
    // EmptyResultAdvisor; this only writes the line to stderr.
    private static void WriteEmptyHint(ScrapeContext ctx, int recordCount)
    {
        var hint = EmptyResultAdvisor.Advise(ctx.Browser, ctx.Stealth, recordCount);
        if (hint is not null)
            Console.Error.WriteLine($"⚠  {hint}");
    }

    // ----- single scrape run -----

    // ADR-0083: BlockedPageCount carries the block detector's run-level tally out
    // of the engine so the caller can warn + exit non-zero on a blocked load.
    internal sealed record AttemptResult(List<ParsedData> Records, string? Output, int BlockedPageCount);

    private static async Task<AttemptResult> RunOnceAsync(
        ScrapeContext ctx, StartTier startTier, bool includeStealth, string? stealthPath)
    {
        // ADR-0040: AsMarkdown is the default; Extract(schema) is the upgrade.
        // The start page's PageType picks the entry rung — Static (Crawl) enters
        // at HTTP, Dynamic (CrawlWithBrowser) enters at the first browser-class
        // rung.
        var seed = startTier == StartTier.Http
            ? ScraperEngineBuilder.Crawl(ctx.Url)
            : ScraperEngineBuilder.CrawlWithBrowser(ctx.Url);

        // ADR-0084: AI strategies (--prompt / --infer) take precedence over the
        // deterministic terminals. The chat client is disposed on method exit,
        // after the engine's RunAsync (declared later, so disposed first).
        using var llm = ctx.Ai.Any ? AiExtraction.CreateClient(ctx.Ai) : null;
        var builder = ctx.Ai.Any
            ? AiExtraction.Apply(seed, ctx.Ai, llm!)
            : ctx.SchemaPath is not null
                ? seed.Extract(SchemaFile.Load(ctx.SchemaPath))
                : seed.AsMarkdown();

        // The vanilla-browser rung — skipped for --stealth, where the stealth
        // rung is the browser-class tier the Dynamic start page enters at. BYO
        // endpoint if given; else a managed Chromium spawn (lazy), registered for
        // an explicit --browser always, and for a plain scrape only when a
        // browser is actually present so a plain HTTP scrape degrades gracefully
        // rather than into a launch error on a browser-less machine.
        if (startTier != StartTier.Stealth)
        {
            if (ctx.CdpUrl is not null)
                builder = builder.WithCdpPageLoader(ctx.CdpUrl);
            else if (startTier == StartTier.Browser || CdpLaunchHelpers.FindOnPath(ChromeNames) is not null)
                builder = builder.WithCdpPageLoader(new CdpLaunchOptions { Headless = true });
        }

        // The stealth rung (top), when included. Appends above the browser rung
        // (for --browser, the climb target) or is the sole browser-class rung
        // (for --stealth, the entry).
        if (includeStealth && stealthPath is not null)
            builder = builder.WithCdpPageLoader(new CdpLaunchOptions
            {
                ExecutablePath = stealthPath,
                Headless = true,
                AdditionalArgs = StealthBackend.LaunchArgs,
            });

        if (ctx.Follow is not null) builder = builder.Follow(ctx.Follow);
        if (ctx.MaxAge is { } age) builder = builder.WithMaxAge(age);

        var records = new List<ParsedData>();
        builder = builder.Subscribe(records.Add).StopWhenAllLinksProcessed();

        // ADR-0058: await using disposes the engine's resources on scope exit,
        // including a lazily-spawned managed Chromium / stealth subprocess.
        // ADR-0083: capture the RunReport so the block tally surfaces to ShipAsync.
        await using var engine = await builder.BuildAsync();
        var report = await engine.RunAsync();

        return new AttemptResult(records, ctx.Output, report.BlockedPageCount);
    }

    // ----- self-invocation helpers -----

    /// <summary>Re-invoke this CLI binary as a subprocess and return its exit
    /// code. The child's stdout, where the install reports its ↓/✓ progress, is
    /// forwarded to our stderr: an inherited stdout is the same file or pipe as
    /// the scrape's own, so the progress lines would land in the scraped output.
    /// The child's stderr is inherited.</summary>
    private static async Task<int> RunSelfAsync(params string[] argv)
    {
        var path = Environment.ProcessPath
            ?? throw new CliException("Cannot resolve own executable path for stealth subprocess.");
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new CliException($"Failed to spawn self ({path}).");
        while (await p.StandardOutput.ReadLineAsync() is { } line)
            Console.Error.WriteLine(line);
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Re-invoke this binary and capture stdout. Returns the first
    /// non-empty line, or null if the subprocess failed or its stdout was
    /// empty.</summary>
    private static async Task<string?> CaptureSelfAsync(params string[] argv)
    {
        var path = Environment.ProcessPath;
        if (path is null) return null;

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null) return null;

        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) return null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    // ----- arg parsing -----

    internal sealed record ScrapeContext(
        string Url,
        string? SchemaPath,
        string? Output,
        TimeSpan? MaxAge,
        string? Follow,
        string? CdpUrl,
        bool Browser,
        bool Stealth,
        bool AutoStealth,
        bool NoAutoStealth,
        AiExtractionContext Ai,
        string? OutputDir,
        bool Open);

    internal static ScrapeContext ParseContext(ParsedArgs args)
    {
        var url = Urls.Normalize(args.Positional[0]);
        var cdpUrl = args.GetFlag("browser-cdp-url");
        var browser = args.HasFlag("browser") || cdpUrl is not null;
        var stealth = args.HasFlag("stealth");
        var autoStealthFlag = args.HasFlag("auto-stealth")
            || string.Equals(Environment.GetEnvironmentVariable("WEBREAPER_AUTO_STEALTH"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("WEBREAPER_AUTO_STEALTH"), "true", StringComparison.OrdinalIgnoreCase);
        var noAutoStealth = args.HasFlag("no-auto-stealth");

        // --stealth without --browser implies --browser (the user opted into a
        // Chromium-based scrape; the only difference is which Chromium).
        if (stealth) browser = true;

        var schemaPath = args.GetFlag("schema");
        var ai = AiExtraction.Parse(args);
        AiExtraction.ValidateExclusive(ai, schemaPath);
        var (output, outputDir, open) = RecordOutput.ParseTarget(args);

        return new ScrapeContext(
            Url: url,
            SchemaPath: schemaPath,
            Output: output,
            MaxAge: args.GetTimeSpanFlag("max-age"),
            Follow: args.GetFlag("follow"),
            CdpUrl: cdpUrl,
            Browser: browser,
            Stealth: stealth,
            AutoStealth: autoStealthFlag,
            NoAutoStealth: noAutoStealth,
            Ai: ai,
            OutputDir: outputDir,
            Open: open);
    }
}
