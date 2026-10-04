namespace WebReaper.Cli.Stealth;

/// <summary>
/// ADR-0055: AOT-friendly static registry of stealth backends the CLI
/// surface knows about. Library satellites (<c>WebReaper.Stealth.X</c>)
/// ship freely for direct library use; CLI integration requires adding
/// a row here via PR. The CLI cannot reflectively discover installed
/// satellites — AOT-published single binary; NuGet has no post-install
/// hooks; a curated static list is the working answer.
/// </summary>
public static class KnownStealthBackends
{
    /// <summary>Every backend the CLI's <c>webreaper stealth install</c>
    /// command can offer. Add new backends via PR.</summary>
    public static readonly StealthBackend[] All =
    [
        new StealthBackend(
            Name: "cloakbrowser",
            DisplayName: "CloakBrowser",
            Description: "58 fingerprint patches; recommended",
            LicenseUrl: "https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md",
            // Releases are tagged `chromium-v<build>`, each with a SHA256SUMS
            // manifest beside its assets.
            ReleaseUrlPattern: "https://github.com/CloakHQ/CloakBrowser/releases/download/chromium-v{version}/{file}",
            ChecksumFile: "SHA256SUMS",
            // One build per platform, mirroring the per-platform pins the
            // vendor's own npm / pip / NuGet wrappers ship: not every release
            // carries every platform, so macOS and linux-arm64 trail. There is
            // no Windows-on-ARM or 32-bit build.
            Builds:
            [
                new("linux-x64", "146.0.7680.177.5", "cloakbrowser-linux-x64.tar.gz", "chrome", SizeMb: 217),
                new("linux-arm64", "146.0.7680.177.3", "cloakbrowser-linux-arm64.tar.gz", "chrome", SizeMb: 208),
                new("osx-arm64", "145.0.7632.109.2", "cloakbrowser-darwin-arm64.tar.gz", "Chromium.app/Contents/MacOS/Chromium", SizeMb: 147),
                new("osx-x64", "145.0.7632.109.2", "cloakbrowser-darwin-x64.tar.gz", "Chromium.app/Contents/MacOS/Chromium", SizeMb: 159),
                new("win-x64", "146.0.7680.177.5", "cloakbrowser-windows-x64.zip", "chrome.exe", SizeMb: 562),
            ],
            // The override and cache the vendor's wrappers use, so a binary
            // from `npm i -g cloakbrowser` / `pip install cloakbrowser` is
            // reused instead of downloaded again.
            BinaryPathEnvVar: "CLOAKBROWSER_BINARY_PATH",
            VendorCache: new VendorCache(
                DirEnvVar: "CLOAKBROWSER_CACHE_DIR",
                DefaultDirName: ".cloakbrowser",
                VersionDirPrefix: "chromium-"),
            LaunchArgs:
            [
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding",
                "--disable-features=TranslateUI,Translate",
                "--disable-dev-shm-usage",
                // Keep Chromium off the macOS login keychain (the switch
                // Playwright and Puppeteer pass). Without it the macOS build
                // stalls at startup and never publishes its CDP endpoint.
                // Ignored on other platforms.
                "--use-mock-keychain",
            ]),
        // Future entries — Patchright, Camoufox, undetected-chromedriver —
        // land here as community PRs alongside their library satellites.
    ];

    /// <summary>Look up a backend by name (case-insensitive); returns
    /// <c>null</c> if not registered.</summary>
    public static StealthBackend? Find(string name) =>
        All.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry in the curated <see cref="KnownStealthBackends"/>
/// list — everything the CLI needs to install + launch a stealth fork
/// without reflectively loading the library satellite.</summary>
/// <param name="Name">Short canonical id (lowercase, no spaces) — what the
/// user types after <c>webreaper stealth install</c>.</param>
/// <param name="DisplayName">Human-friendly name shown in the picker UI.</param>
/// <param name="Description">One-line value-prop for the picker.</param>
/// <param name="LicenseUrl">Binary-license URL shown in the Y/n prompt.</param>
/// <param name="ReleaseUrlPattern">Format string with <c>{version}</c> and
/// <c>{file}</c> placeholders the CLI substitutes to build the URL of a
/// release asset or of its <paramref name="ChecksumFile"/>.</param>
/// <param name="ChecksumFile">The <c>sha256sum</c>-format manifest each
/// release publishes; every download is verified against it.</param>
/// <param name="Builds">The pinned upstream build per platform. A platform
/// with no row has no upstream build.</param>
/// <param name="BinaryPathEnvVar">Env var naming a binary to use as-is
/// (skipping install), or <c>null</c> if the backend has none.</param>
/// <param name="VendorCache">Where the vendor's own installer caches builds,
/// or <c>null</c> if it has none.</param>
/// <param name="LaunchArgs">Vendor-recommended command-line flags the CLI
/// passes to the launched binary (in addition to
/// <c>--remote-debugging-port=0</c> which is added automatically).</param>
public sealed record StealthBackend(
    string Name,
    string DisplayName,
    string Description,
    string LicenseUrl,
    string ReleaseUrlPattern,
    string ChecksumFile,
    IReadOnlyList<StealthBuild> Builds,
    string? BinaryPathEnvVar,
    VendorCache? VendorCache,
    IReadOnlyList<string> LaunchArgs)
{
    /// <summary>The pinned build for <paramref name="platform"/> (a RID from
    /// <see cref="StealthInstaller.CurrentPlatform"/>), or <c>null</c> when
    /// upstream publishes none.</summary>
    public StealthBuild? BuildFor(string? platform) =>
        Builds.FirstOrDefault(b => string.Equals(b.Platform, platform, StringComparison.Ordinal));

    /// <summary>The URL of <paramref name="file"/> (an asset or the
    /// checksum manifest) in the release of <paramref name="version"/>.</summary>
    public string ReleaseUrl(string version, string file) =>
        ReleaseUrlPattern.Replace("{version}", version).Replace("{file}", file);
}

/// <summary>One platform's pinned upstream build of a
/// <see cref="StealthBackend"/>.</summary>
/// <param name="Platform">The RID the build runs on (<c>linux-x64</c>,
/// <c>osx-arm64</c>, <c>win-x64</c>, …).</param>
/// <param name="Version">The upstream build installed by default; override
/// with <c>--version</c>.</param>
/// <param name="Asset">The release asset's file name (<c>.tar.gz</c> or
/// <c>.zip</c>).</param>
/// <param name="Executable">The executable's <c>/</c>-separated path inside
/// the unpacked archive.</param>
/// <param name="SizeMb">Approximate download size for the prompt.</param>
public sealed record StealthBuild(
    string Platform,
    string Version,
    string Asset,
    string Executable,
    int SizeMb);

/// <summary>Where a backend vendor's own installer caches its builds,
/// laid out <c>&lt;dir&gt;/&lt;VersionDirPrefix&gt;&lt;version&gt;/&lt;Executable&gt;</c>.
/// The CLI reuses a build it finds there instead of downloading it again.</summary>
/// <param name="DirEnvVar">Env var that relocates the cache.</param>
/// <param name="DefaultDirName">The cache directory's name under the user's
/// home when <paramref name="DirEnvVar"/> is unset.</param>
/// <param name="VersionDirPrefix">Prefix of each per-version directory.</param>
public sealed record VendorCache(
    string DirEnvVar,
    string DefaultDirName,
    string VersionDirPrefix);
