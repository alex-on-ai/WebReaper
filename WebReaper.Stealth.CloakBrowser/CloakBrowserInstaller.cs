using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace WebReaper.Stealth.CloakBrowser;

/// <summary>
/// CloakBrowser binary acquisition (ADR-0054). Reuse an install that already
/// exists; otherwise download the platform's pinned build from upstream, verify
/// it against the release's checksum manifest, and unpack it. Mirrors
/// <c>playwright install</c>'s on-user-request shape: the binary is fetched
/// from CloakHQ's own servers on the user's machine; nothing is rehosted.
/// </summary>
/// <remarks>
/// <para>
/// Lookup order, first match wins: <see cref="CloakBrowserOptions.ExecutablePath"/>;
/// the vendor's <c>CLOAKBROWSER_BINARY_PATH</c> override; the WebReaper cache
/// <c>~/.webreaper/stealth/cloakbrowser/&lt;build&gt;/</c>; then the cloakbrowser
/// npm / pip wrapper's cache (<c>CLOAKBROWSER_CACHE_DIR</c>, else
/// <c>~/.cloakbrowser/</c>). PATH is not searched: the wrappers put a CLI named
/// <c>cloakbrowser</c> there, not the browser.
/// </para>
/// <para>
/// The CLI's <c>webreaper stealth install</c> re-implements this installer
/// (ADR-0055: the AOT CLI cannot reference satellites) over the same cache
/// layout, so an install made by either is reused by the other. A download is
/// verified against the release's <c>SHA256SUMS</c> and unpacked with BCL APIs
/// (AOT-clean, no external <c>tar</c>); an interrupted one restarts from
/// scratch.
/// </para>
/// </remarks>
public static class CloakBrowserInstaller
{
    /// <summary>The newest free CloakBrowser release, which is the build this
    /// satellite pins on linux-x64 and windows-x64. macOS and linux-arm64 pin
    /// older builds, because upstream publishes no newer free build for them
    /// (see the README). <see cref="CloakBrowserOptions.Version"/> overrides the
    /// pin.</summary>
    public const string DefaultVersion = "chromium-v146.0.7680.177.5";

    /// <summary>The license URL surfaced on first install.</summary>
    public const string LicenseUrl = "https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md";

    /// <summary>The vendor wrappers' override: a CloakBrowser binary to use
    /// as-is.</summary>
    internal const string BinaryPathEnvVar = "CLOAKBROWSER_BINARY_PATH";

    /// <summary>Relocates the vendor wrappers' cache.</summary>
    internal const string CacheDirEnvVar = "CLOAKBROWSER_CACHE_DIR";

    private const string ReleasesBase = "https://github.com/CloakHQ/CloakBrowser/releases/download";
    private const string TagPrefix = "chromium-v";
    private const string ChecksumFile = "SHA256SUMS";
    private const string VendorCacheDirName = ".cloakbrowser";
    private const string VendorVersionDirPrefix = "chromium-";
    private const string PartialPrefix = ".partial-";

    /// <summary>Idempotent: returns the path to a usable CloakBrowser
    /// executable for the current platform. Reuses an existing install (see the
    /// class remarks for the lookup order); if there is none, downloads the
    /// platform's pinned build from upstream, subject to
    /// <see cref="CloakBrowserOptions.AutoInstall"/>.</summary>
    /// <exception cref="ArgumentException"><see cref="CloakBrowserOptions.Version"/>
    /// is not a CloakBrowser build.</exception>
    /// <exception cref="FileNotFoundException"><see cref="CloakBrowserOptions.ExecutablePath"/>
    /// or <c>CLOAKBROWSER_BINARY_PATH</c> names a missing file.</exception>
    /// <exception cref="PlatformNotSupportedException">Upstream publishes no
    /// build for this platform and no binary was supplied.</exception>
    /// <exception cref="InvalidOperationException">No install was found and
    /// <see cref="CloakBrowserOptions.AutoInstall"/> is
    /// <see cref="AutoInstallPolicy.Disabled"/>, or the download failed
    /// verification (in which case nothing is installed).</exception>
    /// <exception cref="HttpRequestException">The release or its asset could
    /// not be fetched.</exception>
    public static async Task<string> EnsureInstalledAsync(
        CloakBrowserOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        return await EnsureInstalledAsync(
            options,
            logger,
            CloakBrowserBuild.CurrentPlatform(),
            GetWebReaperHome(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable,
            () => new HttpClient { Timeout = TimeSpan.FromMinutes(20) },
            ct);
    }

    // The testable core: the platform, both home directories, the environment,
    // and the HTTP client are parameters, so tests run offline against temp dirs.
    internal static async Task<string> EnsureInstalledAsync(
        CloakBrowserOptions options,
        ILogger logger,
        string? platform,
        string webReaperHome,
        string userHome,
        Func<string, string?> getEnv,
        Func<HttpClient> createHttp,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (!File.Exists(options.ExecutablePath))
                throw new FileNotFoundException(
                    $"CloakBrowserOptions.ExecutablePath does not exist: {options.ExecutablePath}");
            return options.ExecutablePath;
        }

        var build = CloakBrowserBuild.For(platform);
        var pinned = NormalizeVersion(options.Version);
        var found = FindInstalled(build, pinned, webReaperHome, userHome, getEnv);
        if (found is not null)
        {
            logger.LogInformation("CloakBrowser: using {Path} ({Source}).", found.Path, Describe(found));
            return found.Path;
        }

        if (build is null)
            throw new PlatformNotSupportedException(NoBuildMessage(platform));

        var version = pinned ?? build.Version;
        var cacheRoot = CacheRoot(webReaperHome);
        if (options.AutoInstall == AutoInstallPolicy.Disabled)
        {
            var vendorDir = VendorCacheDir(userHome, getEnv);
            throw new InvalidOperationException(
                $"CloakBrowser {version} ({build.Platform}) is not installed and AutoInstall is Disabled. " +
                $"Looked in {Path.Combine(cacheRoot, version)}" +
                (vendorDir is null ? ". " : $" and {vendorDir}. ") +
                $"Set CloakBrowserOptions.ExecutablePath or {BinaryPathEnvVar} to a CloakBrowser binary, " +
                "or change AutoInstall to PromptLogger / NoPromptYes to download it.");
        }

        if (options.AutoInstall == AutoInstallPolicy.PromptLogger)
        {
            logger.LogWarning(
                "CloakBrowser: downloading build {Version} for {Platform} from upstream (~{SizeMb} MB). " +
                "By using CloakBrowser you accept its binary license: {LicenseUrl}",
                version, build.Platform, build.SizeMb, LicenseUrl);
        }

        using var http = createHttp();
        return await InstallAsync(build, version, cacheRoot, http, logger, ct);
    }

    /// <summary>The OS-conventional WebReaper home dir
    /// (<c>~/.webreaper/</c> on Unix; <c>%LOCALAPPDATA%/WebReaper/</c> on
    /// Windows). Shared with <see cref="WebReaper.Cdp"/>'s managed-browser
    /// cache.</summary>
    public static string GetWebReaperHome()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "WebReaper");
        }
        var home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
        return Path.Combine(home, ".webreaper");
    }

    /// <summary>The WebReaper cache root, one directory per installed build
    /// below it. The CLI installs into the same place.</summary>
    internal static string CacheRoot(string webReaperHome) =>
        Path.Combine(webReaperHome, "stealth", "cloakbrowser");

    /// <summary>
    /// <see cref="CloakBrowserOptions.Version"/> as a bare build number, or
    /// <c>null</c> when unset. Takes the release tag
    /// (<c>chromium-v146.0.7680.177.5</c>, the form this option documented
    /// first) or the bare build (<c>146.0.7680.177.5</c>, the vendor's and the
    /// CLI's form). The build lands in a URL and a directory name, so it must
    /// look like one: a leading digit, then only letters, digits, <c>.</c>,
    /// <c>-</c> and <c>_</c>, and no <c>..</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Not a build.</exception>
    internal static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var build = version.Trim();
        if (build.StartsWith(TagPrefix, StringComparison.Ordinal)) build = build[TagPrefix.Length..];

        var valid = build.Length is > 0 and <= 64
            && char.IsAsciiDigit(build[0])
            && build.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            && !build.Contains("..", StringComparison.Ordinal);
        return valid
            ? build
            : throw new ArgumentException(
                $"CloakBrowserOptions.Version '{version}' is not a CloakBrowser build. Expected a release tag " +
                $"like {DefaultVersion} or a build like {DefaultVersion[TagPrefix.Length..]}.");
    }

    /// <summary>
    /// Find a usable install without downloading anything, in order:
    /// <list type="number">
    ///   <item><c>CLOAKBROWSER_BINARY_PATH</c>. It wins outright; a value naming
    ///         a missing file is an error, not a fall-through, so a typo never
    ///         silently picks a different binary.</item>
    ///   <item>The WebReaper cache: <paramref name="pinnedVersion"/>, else the
    ///         platform's pinned build.</item>
    ///   <item>The vendor wrapper's cache: <paramref name="pinnedVersion"/>
    ///         when given, else the newest build there.</item>
    /// </list>
    /// With no <paramref name="build"/> for the platform only the override can
    /// match, because the executable's place inside a build is unknown.
    /// </summary>
    /// <exception cref="FileNotFoundException"><c>CLOAKBROWSER_BINARY_PATH</c>
    /// names a missing file.</exception>
    internal static CloakBrowserInstall? FindInstalled(
        CloakBrowserBuild? build,
        string? pinnedVersion,
        string webReaperHome,
        string userHome,
        Func<string, string?> getEnv)
    {
        if (getEnv(BinaryPathEnvVar) is { Length: > 0 } overridePath)
        {
            return File.Exists(overridePath)
                ? new CloakBrowserInstall(overridePath, null, InstallSource.BinaryPathEnvVar)
                : throw new FileNotFoundException(
                    $"{BinaryPathEnvVar} is set to '{overridePath}', but no file exists there. Fix or unset it.",
                    overridePath);
        }

        if (build is null) return null;

        var version = pinnedVersion ?? build.Version;
        var cacheRoot = CacheRoot(webReaperHome);
        // The <build>/ layout the CLI shares, then the chromium-v<build>/ layout
        // earlier releases of this satellite wrote, so an upgrade does not
        // download the same build again.
        foreach (var dirName in new[] { version, TagPrefix + version })
        {
            var cached = ExecutablePath(Path.Combine(cacheRoot, dirName), build);
            if (IsExecutable(cached)) return new CloakBrowserInstall(cached, version, InstallSource.WebReaperCache);
        }

        if (VendorCacheDir(userHome, getEnv) is not { } vendorDir) return null;

        if (pinnedVersion is not null)
        {
            var pinned = ExecutablePath(Path.Combine(vendorDir, VendorVersionDirPrefix + pinnedVersion), build);
            return IsExecutable(pinned)
                ? new CloakBrowserInstall(pinned, pinnedVersion, InstallSource.VendorCache)
                : null;
        }

        return NewestVendorBuild(vendorDir, build);
    }

    // CLOAKBROWSER_CACHE_DIR, else ~/.cloakbrowser, the vendor wrappers' default.
    private static string? VendorCacheDir(string userHome, Func<string, string?> getEnv) =>
        getEnv(CacheDirEnvVar) is { Length: > 0 } relocated ? relocated
        : string.IsNullOrEmpty(userHome) ? null
        : Path.Combine(userHome, VendorCacheDirName);

    // The vendor's wrapper auto-updates into sibling `chromium-<build>` dirs, so
    // take the newest one whose executable is present. Directories whose suffix
    // is not a plain dotted build number (Pro builds, `chromium-<build>-pro`, and
    // strays) are skipped, as are incomplete ones without the executable.
    private static CloakBrowserInstall? NewestVendorBuild(string vendorDir, CloakBrowserBuild build)
    {
        if (!Directory.Exists(vendorDir)) return null;

        CloakBrowserInstall? newest = null;
        long[]? newestParts = null;
        foreach (var dir in Directory.EnumerateDirectories(vendorDir, VendorVersionDirPrefix + "*"))
        {
            var version = Path.GetFileName(dir)[VendorVersionDirPrefix.Length..];
            if (!TryParseBuildNumber(version, out var parts)) continue;
            var exe = ExecutablePath(dir, build);
            if (!IsExecutable(exe)) continue;
            if (newestParts is null || CompareBuildNumbers(parts, newestParts) > 0)
            {
                newest = new CloakBrowserInstall(exe, version, InstallSource.VendorCache);
                newestParts = parts;
            }
        }
        return newest;
    }

    private static bool TryParseBuildNumber(string version, out long[] parts)
    {
        var segments = version.Split('.');
        parts = new long[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || !segments[i].All(char.IsAsciiDigit)
                || !long.TryParse(segments[i], out parts[i]))
                return false;
        }
        return true;
    }

    private static int CompareBuildNumbers(long[] a, long[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var cmp = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>
    /// Download <paramref name="build"/> at <paramref name="version"/>, verify
    /// it against the release's checksum manifest, and unpack it into
    /// <c>&lt;cacheRoot&gt;/&lt;version&gt;/</c>; returns the executable's path.
    /// The manifest is fetched first, so a release that does not carry this
    /// platform fails before the large download. The archive is unpacked into
    /// a scratch directory that is renamed into place only once complete, so
    /// an interrupted install never leaves a half-unpacked build behind.
    /// </summary>
    /// <exception cref="HttpRequestException">The manifest or the archive
    /// could not be fetched.</exception>
    /// <exception cref="InvalidOperationException">The manifest has no entry
    /// for this platform, the checksum does not match, or the archive lacks the
    /// executable. Nothing is installed.</exception>
    internal static async Task<string> InstallAsync(
        CloakBrowserBuild build,
        string version,
        string cacheRoot,
        HttpClient http,
        ILogger logger,
        CancellationToken ct)
    {
        var manifestUrl = ReleaseUrl(version, ChecksumFile);
        string manifest;
        using (var response = await http.GetAsync(manifestUrl, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Could not fetch CloakBrowser {version}'s {ChecksumFile} ({manifestUrl}): " +
                    $"HTTP {(int)response.StatusCode}. Check that the build exists upstream.",
                    inner: null,
                    response.StatusCode);
            manifest = await response.Content.ReadAsStringAsync(ct);
        }

        var expected = ParseChecksum(manifest, build.Asset)
            ?? throw new InvalidOperationException(
                $"CloakBrowser {version} has no {build.Platform} build: its {ChecksumFile} does not list {build.Asset}." +
                (version == build.Version
                    ? ""
                    : $" Unset CloakBrowserOptions.Version to install the pinned {build.Version}."));

        Directory.CreateDirectory(cacheRoot);
        SweepStalePartials(cacheRoot);
        var scratch = Path.Combine(cacheRoot, $"{PartialPrefix}{Guid.NewGuid():N}");
        var archive = scratch + "-" + build.Asset;
        try
        {
            var assetUrl = ReleaseUrl(version, build.Asset);
            logger.LogInformation("CloakBrowser: downloading {Url} (~{SizeMb} MB).", assetUrl, build.SizeMb);
            var actual = await DownloadAsync(http, assetUrl, archive, ct);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"CloakBrowser download checksum mismatch for {build.Asset}: {ChecksumFile} lists {expected}, " +
                    $"the download is {actual}. It may be corrupt or tampered with; nothing was installed.");
            logger.LogInformation("CloakBrowser: SHA-256 verified against {ChecksumFile}; extracting.", ChecksumFile);

            await ExtractAsync(archive, build.Asset, scratch, ct);

            var unpacked = ExecutablePath(scratch, build);
            if (!File.Exists(unpacked))
                throw new InvalidOperationException(
                    $"{build.Asset} has no {build.Executable}; the upstream archive layout may have changed.");
            if (!OperatingSystem.IsWindows()) EnsureExecutable(unpacked);

            var target = Path.Combine(cacheRoot, version);
            MoveIntoPlace(scratch, target, build);
            return ExecutablePath(target, build);
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(scratch); // no-op once renamed into place
        }
    }

    /// <summary>The SHA-256 (lowercase hex) that a <c>sha256sum</c>-format
    /// manifest lists for <paramref name="fileName"/>, or <c>null</c> when it
    /// lists none. Lines are <c>&lt;hex&gt;  &lt;file&gt;</c> (a <c>*</c> before
    /// the name marks binary mode); anything else, such as CloakBrowser's
    /// <c>version=&lt;build&gt;</c> header, is ignored.</summary>
    internal static string? ParseChecksum(string manifest, string fileName)
    {
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            var gap = line.IndexOfAny([' ', '\t']);
            if (gap != 64 || !line[..gap].All(char.IsAsciiHexDigit)) continue;
            var name = line[gap..].TrimStart();
            if (name.StartsWith('*')) name = name[1..];
            if (string.Equals(name, fileName, StringComparison.Ordinal))
                return line[..gap].ToLowerInvariant();
        }
        return null;
    }

    internal static string NoBuildMessage(string? platform) =>
        $"CloakBrowser publishes no build for {platform ?? "this platform"} " +
        $"(builds: {string.Join(", ", CloakBrowserBuild.All.Select(b => b.Platform))}). " +
        $"Set CloakBrowserOptions.ExecutablePath or {BinaryPathEnvVar} to a CloakBrowser binary you supply.";

    private static string ReleaseUrl(string version, string file) =>
        $"{ReleasesBase}/{TagPrefix}{version}/{file}";

    private static string Describe(CloakBrowserInstall install) => install.Source switch
    {
        InstallSource.BinaryPathEnvVar => BinaryPathEnvVar,
        InstallSource.WebReaperCache => $"build {install.Version}, WebReaper cache",
        _ => $"build {install.Version}, cloakbrowser wrapper cache",
    };

    // Streams the download to disk, hashing as it goes (the archives run to
    // hundreds of MB, so no second read). Returns the lowercase hex SHA-256.
    private static async Task<string> DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"CloakBrowser download failed ({url}): HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            sha256.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    private static async Task ExtractAsync(string archive, string assetName, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }
        if (!assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported CloakBrowser archive type: {assetName}.");

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        // TarFile keeps each entry's Unix mode (the executable bits) and
        // recreates the symlinks inside a macOS .app bundle's frameworks.
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
    }

    // A killed install (say, a host stopped during a 560 MB download) leaves its
    // archive or scratch dir behind. Anything untouched for an hour is dead: a
    // live install keeps writing, so a concurrent one is never swept.
    private static void SweepStalePartials(string cacheRoot)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var entry in new DirectoryInfo(cacheRoot).EnumerateFileSystemInfos(PartialPrefix + "*"))
        {
            if (entry.LastWriteTimeUtc >= cutoff) continue;
            if (entry is DirectoryInfo dir) TryDeleteDirectory(dir.FullName);
            else TryDelete(entry.FullName);
        }
    }

    // A target that already holds a usable build (a concurrent install got there
    // first) is kept; one without the executable is a stale partial and is
    // replaced.
    private static void MoveIntoPlace(string unpacked, string target, CloakBrowserBuild build)
    {
        if (Directory.Exists(target))
        {
            if (IsExecutable(ExecutablePath(target, build))) return;
            Directory.Delete(target, recursive: true);
        }
        try
        {
            Directory.Move(unpacked, target);
        }
        catch (IOException) when (IsExecutable(ExecutablePath(target, build)))
        {
            // Lost a rename race to a concurrent install of the same build.
        }
    }

    private static string ExecutablePath(string buildDir, CloakBrowserBuild build) =>
        Path.Combine([buildDir, .. build.Executable.Split('/')]);

    private static bool IsExecutable(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows() || HasExecuteBit(path));

    // The Unix file-mode APIs throw on Windows; every caller is guarded by
    // OperatingSystem.IsWindows(), which these attributes let CA1416 see.
    [UnsupportedOSPlatform("windows")]
    private static bool HasExecuteBit(string path) =>
        (File.GetUnixFileMode(path)
         & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    [UnsupportedOSPlatform("windows")]
    private static void EnsureExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>Where a found CloakBrowser binary came from.</summary>
internal enum InstallSource
{
    /// <summary>The vendor's <c>CLOAKBROWSER_BINARY_PATH</c> override.</summary>
    BinaryPathEnvVar,

    /// <summary>The WebReaper cache,
    /// <c>~/.webreaper/stealth/cloakbrowser/</c>.</summary>
    WebReaperCache,

    /// <summary>The cloakbrowser npm / pip wrapper's cache,
    /// <c>~/.cloakbrowser/</c>.</summary>
    VendorCache,
}

/// <summary>A usable CloakBrowser binary on disk. <paramref name="Version"/>
/// is <c>null</c> for the <c>CLOAKBROWSER_BINARY_PATH</c> override, whose build
/// is unknown.</summary>
internal sealed record CloakBrowserInstall(string Path, string? Version, InstallSource Source);
