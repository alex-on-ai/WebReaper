using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WebReaper.Cdp;

namespace WebReaper.Stealth.CloakBrowser;

/// <summary>
/// CloakBrowser binary acquisition (ADR-0054). Detect first, download from
/// upstream second, log license-acknowledgment on first download. Mirrors
/// <c>playwright install</c>'s on-user-request shape — the binary is
/// fetched from CloakHQ's own servers on the user's machine; nothing
/// rehosted.
/// </summary>
/// <remarks>
/// The download resolves a real GitHub release asset, verifies it against the
/// release's published <c>SHA256SUMS</c>, and extracts it with BCL APIs (no
/// external <c>tar</c>). Resumable download is still a TODO; a partial-file
/// failure retries from scratch. Only the platforms CloakBrowser actually ships
/// (<c>linux-x64</c>, <c>windows-x64</c>) can auto-download; other RIDs must
/// supply <see cref="CloakBrowserOptions.ExecutablePath"/>.
/// </remarks>
public static class CloakBrowserInstaller
{
    /// <summary>The release tag this satellite installs unless the consumer
    /// overrides via <see cref="CloakBrowserOptions.Version"/>. CloakBrowser tags
    /// its stealth builds <c>chromium-vN</c>.</summary>
    public const string DefaultVersion = "chromium-v146.0.7680.177.5";

    /// <summary>The license URL surfaced on first install.</summary>
    public const string LicenseUrl = "https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md";

    private const string ReleasesBase = "https://github.com/CloakHQ/CloakBrowser/releases/download";

    /// <summary>Idempotent: returns the path to a usable CloakBrowser
    /// executable for the current RID. Detects an existing install
    /// (PATH + the satellite's cache dir <c>~/.webreaper/stealth/cloakbrowser/</c>)
    /// first; downloads from upstream if absent.</summary>
    public static async Task<string> EnsureInstalledAsync(
        CloakBrowserOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (!File.Exists(options.ExecutablePath))
                throw new FileNotFoundException(
                    $"CloakBrowserOptions.ExecutablePath does not exist: {options.ExecutablePath}");
            return options.ExecutablePath;
        }

        // Resolve the upstream asset for this platform first, so an unsupported
        // RID (macOS, linux-arm64, win-x86, none of which CloakBrowser ships)
        // fails fast with an actionable message instead of a 404 mid-download.
        var asset = ResolveAsset();

        var version = options.Version ?? DefaultVersion;
        var cacheDir = Path.Combine(GetWebReaperHome(), "stealth", "cloakbrowser", version);
        var cachedBinary = Path.Combine(cacheDir, asset.ExeName);
        if (File.Exists(cachedBinary)) return cachedBinary;

        // Try PATH detection — some users may have CloakBrowser installed
        // system-wide (e.g. via the vendor's own installer).
        var onPath = CdpLaunchHelpers.FindOnPath("cloakbrowser", "cloak-browser");
        if (onPath is not null)
        {
            logger.LogInformation("CloakBrowser: found pre-installed binary on PATH ({Path}); skipping download.", onPath);
            return onPath;
        }

        if (options.AutoInstall == AutoInstallPolicy.Disabled)
        {
            throw new InvalidOperationException(
                "CloakBrowser binary not found and AutoInstall is Disabled. " +
                "Either install CloakBrowser yourself and place the binary on PATH, " +
                "or set CloakBrowserOptions.ExecutablePath, or change AutoInstall to PromptLogger / NoPromptYes.");
        }

        if (options.AutoInstall == AutoInstallPolicy.PromptLogger)
        {
            logger.LogWarning(
                "CloakBrowser: downloading binary {Version} from upstream (~220 MB). " +
                "By using CloakBrowser you accept its binary license: {LicenseUrl}",
                version, LicenseUrl);
        }

        Directory.CreateDirectory(cacheDir);
        await DownloadAndExtractAsync(version, asset, cacheDir, logger, ct);

        if (!File.Exists(cachedBinary))
            throw new InvalidOperationException(
                $"CloakBrowser install completed but expected binary not found at {cachedBinary}.");

        if (!OperatingSystem.IsWindows())
            EnsureExecutable(cachedBinary);

        return cachedBinary;
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

    // The release asset for the current platform. CloakBrowser publishes only
    // linux-x64 (tar.gz) and windows-x64 (zip); both unpack flat, with the
    // executable named `chrome` / `chrome.exe` (a Chromium fork). The Linux asset
    // is verified; the Windows exe name mirrors it.
    private readonly record struct Asset(string FileName, ArchiveKind Kind, string ExeName);

    private enum ArchiveKind { TarGz, Zip }

    private static Asset ResolveAsset()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsLinux() && arch == Architecture.X64)
            return new Asset("cloakbrowser-linux-x64.tar.gz", ArchiveKind.TarGz, "chrome");
        if (OperatingSystem.IsWindows() && arch == Architecture.X64)
            return new Asset("cloakbrowser-windows-x64.zip", ArchiveKind.Zip, "chrome.exe");

        throw new PlatformNotSupportedException(
            $"CloakBrowser publishes only linux-x64 and windows-x64 binaries; the current platform " +
            $"({(OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription)} / {arch}) has no upstream build. " +
            "Set CloakBrowserOptions.ExecutablePath to a binary you supply, or run on linux-x64 / windows-x64.");
    }

    private static async Task DownloadAndExtractAsync(
        string version, Asset asset, string cacheDir, ILogger logger, CancellationToken ct)
    {
        var assetUrl = $"{ReleasesBase}/{version}/{asset.FileName}";
        var sumsUrl = $"{ReleasesBase}/{version}/SHA256SUMS";
        var archivePath = Path.Combine(cacheDir, asset.FileName);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        logger.LogInformation("CloakBrowser: downloading {Url}", assetUrl);
        using (var resp = await http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(archivePath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        // Verify against the release's published SHA256SUMS before extracting, so
        // a corrupt or tampered download never reaches the launch path.
        var sums = await http.GetStringAsync(sumsUrl, ct);
        var expected = ParseExpectedSha(sums, asset.FileName)
            ?? throw new InvalidOperationException(
                $"CloakBrowser: SHA256SUMS at {sumsUrl} has no entry for {asset.FileName}.");
        var actual = await ComputeSha256Async(archivePath, ct);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(archivePath);
            throw new InvalidOperationException(
                $"CloakBrowser download checksum mismatch for {asset.FileName}: expected {expected}, got {actual}. " +
                "The download may be corrupt or tampered with; it was not extracted.");
        }
        logger.LogInformation("CloakBrowser: SHA256 verified ({Asset}); extracting.", asset.FileName);

        await ExtractAsync(archivePath, cacheDir, asset.Kind, ct);
        TryDelete(archivePath);
    }

    // SHA256SUMS lines are "<hex>  <filename>". Returns the hex for assetName, or
    // null if absent. Internal for unit testing without a network round-trip.
    internal static string? ParseExpectedSha(string sumsContent, string assetName)
    {
        foreach (var line in sumsContent.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1], assetName, StringComparison.Ordinal))
                return parts[0];
        }
        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ExtractAsync(string archivePath, string destDir, ArchiveKind kind, CancellationToken ct)
    {
        if (kind == ArchiveKind.Zip)
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
            return;
        }

        await using var file = File.OpenRead(archivePath);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        // TarFile preserves the entries' Unix permission bits, so the extracted
        // `chrome` keeps its executable mode.
        await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, ct);
    }

    // The Unix file-mode APIs throw on Windows; the only caller is guarded by
    // `if (!OperatingSystem.IsWindows())`, which this attribute lets CA1416 see.
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
}
