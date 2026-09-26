using WebReaper.Cli.Stealth;

namespace WebReaper.Cli.Commands;

/// <summary>
/// ADR-0055: <c>webreaper stealth install [&lt;backend&gt;]</c> — stealth-fork
/// acquisition. Walks <see cref="KnownStealthBackends"/>; interactive
/// picker by default; <c>--yes</c> for unattended; per-backend
/// <c>--version</c> pin. Downloads from each backend's official upstream
/// URL (legal model = <c>playwright install</c>).
/// </summary>
/// <remarks>
/// <c>install</c> and <c>path</c> first look for an existing install (the
/// backend's binary-path env var, the CLI cache, then the vendor's own
/// installer cache; see <see cref="StealthInstaller.FindInstalled"/>), so
/// <c>install</c> is idempotent and never re-downloads a binary the user
/// already has. The picker UI flips to non-interactive (or fails fast) when
/// <c>--yes</c> is set or <c>WEBREAPER_AUTO_STEALTH=1</c> in env.
/// </remarks>
internal static class StealthCommand
{
    public static async Task<int> RunAsync(ParsedArgs args)
    {
        var sub = args.Positional.Count > 0 ? args.Positional[0] : null;

        return sub switch
        {
            "install" => await InstallAsync(args),
            "path" => Path(args),
            "list" => List(),
            _ => Usage(),
        };
    }

    private static async Task<int> InstallAsync(ParsedArgs args)
    {
        // Optional second positional: the backend name to install. Falls
        // back to the interactive picker.
        var requested = args.Positional.Count > 1 ? args.Positional[1] : null;
        var unattended = args.HasFlag("yes") || EnvIsTrue("WEBREAPER_AUTO_STEALTH");

        StealthBackend? backend;
        if (requested is not null)
        {
            backend = KnownStealthBackends.Find(requested);
            if (backend is null)
            {
                Console.Error.WriteLine($"Unknown stealth backend: {requested}");
                Console.Error.WriteLine("Known backends:");
                foreach (var b in KnownStealthBackends.All)
                    Console.Error.WriteLine($"  • {b.Name}");
                return 2;
            }
        }
        else
        {
            if (unattended)
            {
                Console.Error.WriteLine(
                    "Unattended install requires a backend name: webreaper stealth install <name> --yes");
                return 2;
            }
            backend = PromptPicker();
            if (backend is null) return 1;
        }

        var platform = StealthInstaller.CurrentPlatform();
        var build = backend.BuildFor(platform);
        var pinned = PinnedVersion(args, build);

        // An install the user already has (env override, the CLI cache, or the
        // vendor's own installer cache) is reused rather than downloaded again.
        var existing = FindInstalled(backend, build, pinned);
        if (existing is not null)
        {
            Console.WriteLine($"✓ {backend.DisplayName} already installed ({Describe(backend, existing)}): {existing.Path}");
            return 0;
        }

        if (build is null)
        {
            Console.Error.WriteLine($"✗ {NoBuildMessage(backend, platform)}");
            return 1;
        }

        var version = pinned ?? build.Version;
        if (!unattended)
        {
            Console.WriteLine();
            Console.WriteLine($"  {backend.DisplayName} {version} ({build.Platform})");
            Console.WriteLine($"  Size:    ~{build.SizeMb} MB");
            Console.WriteLine($"  License: {backend.LicenseUrl}");
            Console.WriteLine();
            Console.Write($"By using {backend.DisplayName} you accept its binary license. Proceed? [Y/n] ");
            var reply = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(reply) && !reply.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted.");
                return 1;
            }
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            var binary = await StealthInstaller.InstallAsync(
                backend, build, version,
                StealthInstaller.CacheRoot(backend, BrowserCommand.GetWebReaperHome()),
                http, Console.Out);
            Console.WriteLine($"✓ Installed: {binary}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ Install failed: {ex.Message}");
            return 1;
        }
    }

    private static int Path(ParsedArgs args)
    {
        var name = args.Positional.Count > 1 ? args.Positional[1] : null;
        if (name is null) { Console.Error.WriteLine("Usage: webreaper stealth path <backend> [--version V]"); return 2; }
        var backend = KnownStealthBackends.Find(name);
        if (backend is null) { Console.Error.WriteLine($"Unknown backend: {name}"); return 2; }

        var platform = StealthInstaller.CurrentPlatform();
        var build = backend.BuildFor(platform);
        var pinned = PinnedVersion(args, build);

        var found = FindInstalled(backend, build, pinned);
        if (found is not null)
        {
            Console.WriteLine(found.Path);
            return 0;
        }

        if (build is null)
        {
            Console.Error.WriteLine(NoBuildMessage(backend, platform));
            return 1;
        }
        var versionFlag = pinned is null ? "" : $" --version {pinned}";
        Console.Error.WriteLine(
            $"{backend.DisplayName} {pinned ?? build.Version} not installed. Run: webreaper stealth install {backend.Name}{versionFlag}");
        return 1;
    }

    private static int List()
    {
        var platform = StealthInstaller.CurrentPlatform();
        Console.WriteLine("Available stealth backends (curated; install with `webreaper stealth install <name>`):");
        foreach (var b in KnownStealthBackends.All)
        {
            Console.WriteLine($"  • {b.Name,-15} {b.DisplayName,-15} {BuildSummary(b, platform)}  {b.Description}");
            Console.WriteLine($"    {"",-15} {InstallStatus(b, b.BuildFor(platform))}");
        }
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage: webreaper stealth <subcommand>
              install [<backend>] [--version V] [--yes]
                                       Download from upstream; interactive picker
                                       by default; --yes (or WEBREAPER_AUTO_STEALTH=1)
                                       for unattended. Reuses an existing install.
              path    <backend> [--version V]
                                       Print the binary path
              list                     List available curated backends

            CloakBrowser is looked up, in order, at CLOAKBROWSER_BINARY_PATH (used
            as-is), the CLI cache (~/.webreaper/stealth/cloakbrowser/), and the
            cloakbrowser npm/pip wrapper's cache (~/.cloakbrowser/, or
            CLOAKBROWSER_CACHE_DIR); install downloads only when all three miss.

            Curated backends are the ones the CLI can install. Library satellites
            (WebReaper.Stealth.X) ship freely; CLI integration is a small PR per backend.
            """);
        return 2;
    }

    private static StealthBackend? PromptPicker()
    {
        var platform = StealthInstaller.CurrentPlatform();
        Console.WriteLine("Available stealth backends (downloaded from upstream; not bundled):");
        for (var i = 0; i < KnownStealthBackends.All.Length; i++)
        {
            var b = KnownStealthBackends.All[i];
            Console.WriteLine($"  [{i + 1}] {b.DisplayName,-15} {BuildSummary(b, platform)}  {b.Description}");
        }
        Console.Write($"Choice [1]: ");
        var reply = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(reply)) return KnownStealthBackends.All[0];
        if (!int.TryParse(reply, out var n) || n < 1 || n > KnownStealthBackends.All.Length)
        {
            Console.Error.WriteLine($"Invalid choice '{reply}'.");
            return null;
        }
        return KnownStealthBackends.All[n - 1];
    }

    // The validated --version pin, or null for the platform's pinned build.
    private static string? PinnedVersion(ParsedArgs args, StealthBuild? build)
    {
        var pinned = args.GetFlag("version");
        if (pinned is null || StealthInstaller.IsValidVersion(pinned)) return pinned;
        var example = build is null ? "" : $" like {build.Version}";
        throw new CliException($"Invalid --version '{pinned}': expected a build number{example}.");
    }

    private static StealthInstall? FindInstalled(StealthBackend backend, StealthBuild? build, string? pinned) =>
        StealthInstaller.FindInstalled(
            backend, build, pinned,
            BrowserCommand.GetWebReaperHome(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable);

    private static string Describe(StealthBackend backend, StealthInstall install) => install.Source switch
    {
        InstallSource.EnvOverride => $"via {backend.BinaryPathEnvVar}",
        InstallSource.WebReaperCache => $"{install.Version}, WebReaper cache",
        _ => $"{install.Version}, {backend.DisplayName} wrapper cache",
    };

    private static string BuildSummary(StealthBackend backend, string? platform) =>
        backend.BuildFor(platform) is { } build
            ? $"{build.Version} (~{build.SizeMb} MB)"
            : platform is null ? "no build for this platform" : $"no {platform} build";

    private static string InstallStatus(StealthBackend backend, StealthBuild? build)
    {
        try
        {
            return FindInstalled(backend, build, pinned: null) is { } install
                ? $"installed ({Describe(backend, install)}): {install.Path}"
                : "not installed";
        }
        catch (CliException ex)
        {
            return ex.Message;
        }
    }

    internal static string NoBuildMessage(StealthBackend backend, string? platform)
    {
        var builds = string.Join(", ", backend.Builds.Select(b => b.Platform));
        var suggestion = backend.BinaryPathEnvVar is { } overrideVar
            ? $"Point {overrideVar} at a {backend.DisplayName} binary you supply, or"
            : "Instead,";
        return $"{backend.DisplayName} publishes no build for {platform ?? "this platform"} (builds: {builds}). " +
               $"{suggestion} scrape with --browser-cdp-url against a browser you run.";
    }

    private static bool EnvIsTrue(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
