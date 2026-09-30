using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace WebReaper.Cli.Stealth;

/// <summary>Where a resolved stealth binary came from.</summary>
internal enum InstallSource
{
    /// <summary>The backend's binary-path env var
    /// (<see cref="StealthBackend.BinaryPathEnvVar"/>).</summary>
    EnvOverride,

    /// <summary>The CLI's own cache,
    /// <c>~/.webreaper/stealth/&lt;backend&gt;/&lt;version&gt;/</c>.</summary>
    WebReaperCache,

    /// <summary>The vendor's own installer cache
    /// (<see cref="StealthBackend.VendorCache"/>), e.g. <c>~/.cloakbrowser/</c>
    /// populated by <c>npm i -g cloakbrowser</c>.</summary>
    VendorCache,
}

/// <summary>A usable stealth binary on disk. <paramref name="Version"/> is
/// <c>null</c> for an env override, whose build is unknown.</summary>
internal sealed record StealthInstall(string Path, string? Version, InstallSource Source);

/// <summary>
/// Stealth-backend acquisition for the CLI (ADR-0055): find an install that
/// already exists, or download the pinned upstream build, verify it against
/// the release's checksum manifest, and unpack it into the CLI cache. Driven
/// entirely by a <see cref="KnownStealthBackends"/> row. The CLI cannot
/// reference the <c>WebReaper.Stealth.*</c> satellites (ADR-0055), so this is
/// the AOT binary's counterpart of <c>CloakBrowserInstaller</c>, BCL-only
/// (System.Formats.Tar, System.IO.Compression, SHA-256).
/// </summary>
internal static class StealthInstaller
{
    /// <summary>The current machine as a RID (<c>osx-arm64</c>,
    /// <c>linux-x64</c>, …), or <c>null</c> on an OS or architecture the CLI
    /// has no name for.</summary>
    public static string? CurrentPlatform() => PlatformKey(
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsLinux() ? "linux"
        : null,
        RuntimeInformation.OSArchitecture);

    internal static string? PlatformKey(string? os, Architecture arch)
    {
        var cpu = arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null,
        };
        return os is null || cpu is null ? null : $"{os}-{cpu}";
    }

    /// <summary>The CLI's cache root for <paramref name="backend"/>:
    /// <c>&lt;webReaperHome&gt;/stealth/&lt;name&gt;</c>, one directory per
    /// installed version below it.</summary>
    public static string CacheRoot(StealthBackend backend, string webReaperHome) =>
        Path.Combine(webReaperHome, "stealth", backend.Name);

    /// <summary>A <c>--version</c> value is used in a URL and a directory
    /// name, so it must look like a build number: leading digit, then only
    /// letters, digits, <c>.</c>, <c>-</c>, <c>_</c>, and no <c>..</c>.</summary>
    public static bool IsValidVersion(string version) =>
        version.Length is > 0 and <= 64
        && char.IsAsciiDigit(version[0])
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !version.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Find a usable install without downloading anything. In order:
    /// <list type="number">
    ///   <item>The backend's binary-path env var. It wins outright; a value
    ///         naming a missing file is an error, not a fall-through, so a
    ///         typo never silently downloads a different binary.</item>
    ///   <item>The CLI cache: <paramref name="pinnedVersion"/>, else the
    ///         platform's pinned build.</item>
    ///   <item>The vendor's installer cache: <paramref name="pinnedVersion"/>
    ///         when given, else the newest build there.</item>
    /// </list>
    /// With no <paramref name="build"/> for this platform only the env
    /// override can match. PATH is deliberately not searched: the vendor's npm
    /// wrapper puts a Node CLI named <c>cloakbrowser</c> there, not the browser.
    /// </summary>
    /// <exception cref="CliException">The binary-path env var names a
    /// missing file.</exception>
    public static StealthInstall? FindInstalled(
        StealthBackend backend,
        StealthBuild? build,
        string? pinnedVersion,
        string webReaperHome,
        string userHome,
        Func<string, string?> getEnv)
    {
        if (backend.BinaryPathEnvVar is { } overrideVar
            && getEnv(overrideVar) is { Length: > 0 } overridePath)
        {
            return File.Exists(overridePath)
                ? new StealthInstall(overridePath, null, InstallSource.EnvOverride)
                : throw new CliException(
                    $"{overrideVar} is set to '{overridePath}', but no file exists there. Fix or unset it.");
        }

        if (build is null) return null;

        var version = pinnedVersion ?? build.Version;
        var cached = ExecutablePath(Path.Combine(CacheRoot(backend, webReaperHome), version), build);
        if (IsExecutable(cached)) return new StealthInstall(cached, version, InstallSource.WebReaperCache);

        if (backend.VendorCache is not { } vendor) return null;
        var vendorDir = getEnv(vendor.DirEnvVar) is { Length: > 0 } relocated
            ? relocated
            : Path.Combine(userHome, vendor.DefaultDirName);

        if (pinnedVersion is not null)
        {
            var pinned = ExecutablePath(Path.Combine(vendorDir, vendor.VersionDirPrefix + pinnedVersion), build);
            return IsExecutable(pinned) ? new StealthInstall(pinned, pinnedVersion, InstallSource.VendorCache) : null;
        }

        return NewestVendorBuild(vendorDir, vendor, build);
    }

    // The vendor's wrapper auto-updates into sibling `chromium-<build>` dirs, so
    // take the newest one whose executable is present. Directories whose suffix
    // is not a plain dotted build number (Pro builds, `chromium-<build>-pro`, and
    // strays) are skipped, as are incomplete ones without the executable.
    private static StealthInstall? NewestVendorBuild(string vendorDir, VendorCache vendor, StealthBuild build)
    {
        if (!Directory.Exists(vendorDir)) return null;

        StealthInstall? newest = null;
        long[]? newestParts = null;
        foreach (var dir in Directory.EnumerateDirectories(vendorDir, vendor.VersionDirPrefix + "*"))
        {
            var version = Path.GetFileName(dir)[vendor.VersionDirPrefix.Length..];
            if (!TryParseBuildNumber(version, out var parts)) continue;
            var exe = ExecutablePath(dir, build);
            if (!IsExecutable(exe)) continue;
            if (newestParts is null || CompareBuildNumbers(parts, newestParts) > 0)
            {
                newest = new StealthInstall(exe, version, InstallSource.VendorCache);
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
    /// <exception cref="InvalidOperationException">The release, its manifest
    /// entry, or the expected executable is missing, or the checksum does not
    /// match. Nothing is installed.</exception>
    public static async Task<string> InstallAsync(
        StealthBackend backend,
        StealthBuild build,
        string version,
        string cacheRoot,
        HttpClient http,
        TextWriter log,
        CancellationToken ct = default)
    {
        var manifestUrl = backend.ReleaseUrl(version, backend.ChecksumFile);
        string manifest;
        using (var response = await http.GetAsync(manifestUrl, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"could not fetch {backend.DisplayName} {version}'s {backend.ChecksumFile} " +
                    $"({manifestUrl}): HTTP {(int)response.StatusCode}. Check that the version exists upstream.");
            manifest = await response.Content.ReadAsStringAsync(ct);
        }

        var expected = ParseChecksum(manifest, build.Asset)
            ?? throw new InvalidOperationException(
                $"{backend.DisplayName} {version} has no {build.Platform} build: its {backend.ChecksumFile} " +
                $"does not list {build.Asset}." +
                (version == build.Version ? "" : $" Omit --version to install the pinned {build.Version}."));

        Directory.CreateDirectory(cacheRoot);
        SweepStalePartials(cacheRoot);
        var scratch = Path.Combine(cacheRoot, $"{PartialPrefix}{Guid.NewGuid():N}");
        var archive = scratch + "-" + build.Asset;
        try
        {
            var assetUrl = backend.ReleaseUrl(version, build.Asset);
            log.WriteLine($"↓ Downloading {backend.DisplayName} {version} ({build.Platform}, ~{build.SizeMb} MB) from {assetUrl}");
            var actual = await DownloadAsync(http, assetUrl, archive, ct);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"checksum mismatch for {build.Asset}: {backend.ChecksumFile} lists {expected}, the download is {actual}. " +
                    "It may be corrupt or tampered with; nothing was installed.");
            log.WriteLine($"✓ SHA-256 verified against {backend.ChecksumFile}");

            var target = Path.Combine(cacheRoot, version);
            log.WriteLine($"↓ Extracting to {target}");
            await ExtractAsync(archive, build.Asset, scratch, ct);

            var unpacked = ExecutablePath(scratch, build);
            if (!File.Exists(unpacked))
                throw new InvalidOperationException(
                    $"{build.Asset} has no {build.Executable}; the upstream archive layout may have changed. Please report this.");
            if (!OperatingSystem.IsWindows()) EnsureExecutable(unpacked);

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

    // Streams the download to disk, hashing as it goes (the archives run to
    // hundreds of MB, so no second read). Returns the lowercase hex SHA-256.
    private static async Task<string> DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"download failed ({url}): HTTP {(int)response.StatusCode}.");

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
            throw new InvalidOperationException($"unsupported archive type: {assetName}.");

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        // TarFile keeps each entry's Unix mode (the executable bits) and
        // recreates the symlinks inside a macOS .app bundle's frameworks.
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
    }

    private const string PartialPrefix = ".partial-";

    // A killed install (say, Ctrl+C during a 560 MB download) leaves its
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
    private static void MoveIntoPlace(string unpacked, string target, StealthBuild build)
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

    private static string ExecutablePath(string buildDir, StealthBuild build) =>
        Path.Combine([buildDir, .. build.Executable.Split('/')]);

    private static bool IsExecutable(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows() || HasExecuteBit(path));

    // The Unix file-mode APIs throw on Windows; both callers are guarded by
    // OperatingSystem.IsWindows(), which this attribute lets CA1416 see.
    [UnsupportedOSPlatform("windows")]
    private static bool HasExecuteBit(string path) =>
        (File.GetUnixFileMode(path)
         & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    [UnsupportedOSPlatform("windows")]
    private static void EnsureExecutable(string path) =>
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}
