using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebReaper.Stealth.CloakBrowser.Tests;

/// <summary>
/// ADR-0054 amendment: the CloakBrowser installer at parity with the CLI's
/// (#264). Existing-install discovery (the <c>CLOAKBROWSER_BINARY_PATH</c>
/// override, the WebReaper cache, the vendor wrapper's cache) runs against
/// temp directories; the download path runs against a stub
/// <see cref="HttpMessageHandler"/> serving an in-memory release. No test
/// touches the network or the real home directory. The real download and
/// launch is the env-gated <c>CloakBrowserSmokeTests</c> in the integration
/// project.
/// </summary>
public sealed class CloakBrowserInstallerTests : IDisposable
{
    // Builds are looked up by platform name, not by the test machine's
    // platform, so every OS exercises the same layouts.
    private static readonly CloakBrowserBuild Linux = CloakBrowserBuild.For("linux-x64")!;
    private static readonly CloakBrowserBuild Mac = CloakBrowserBuild.For("osx-arm64")!;
    private static readonly CloakBrowserBuild Windows = CloakBrowserBuild.For("win-x64")!;

    private const UnixFileMode Regular =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Executable =
        Regular | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wr-cloak-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _env = new();

    private string WebReaperHome => Path.Combine(_root, "webreaper");
    private string UserHome => Path.Combine(_root, "home");
    // Spelled out rather than computed: the layout is the contract shared with
    // the CLI's installer.
    private string CacheRoot => Path.Combine(WebReaperHome, "stealth", "cloakbrowser");
    private string VendorDir => Path.Combine(UserHome, ".cloakbrowser");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ----- existing-install discovery -----

    private CloakBrowserInstall? Find(CloakBrowserBuild? build, string? pinned = null) =>
        CloakBrowserInstaller.FindInstalled(build, pinned, WebReaperHome, UserHome, name => _env.GetValueOrDefault(name));

    private static string CreateExecutable(params string[] pathParts)
    {
        var path = Path.Combine(pathParts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, Executable);
        return path;
    }

    [Fact]
    public void Nothing_installed_finds_nothing() => Assert.Null(Find(Linux));

    [Fact]
    public void Finds_the_pinned_build_in_the_webreaper_cache()
    {
        var exe = CreateExecutable(CacheRoot, Linux.Version, "chrome");

        Assert.Equal(new CloakBrowserInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
    }

    [Fact]
    public void Finds_a_build_an_earlier_release_of_the_satellite_installed()
    {
        // Up to 11.3 the satellite unpacked into chromium-v<build>/; an upgrade
        // must not download the same build again.
        var legacy = CreateExecutable(CacheRoot, $"chromium-v{Linux.Version}", "chrome");
        Assert.Equal(new CloakBrowserInstall(legacy, Linux.Version, InstallSource.WebReaperCache), Find(Linux));

        // The <build>/ layout the CLI shares wins when both exist.
        var shared = CreateExecutable(CacheRoot, Linux.Version, "chrome");
        Assert.Equal(shared, Find(Linux)?.Path);
    }

    [Fact]
    public void Reuses_a_build_from_the_vendor_wrapper_cache()
    {
        // The #264 layout: the cloakbrowser npm wrapper unpacked the browser to
        // ~/.cloakbrowser/chromium-<build>/chrome.
        var exe = CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");

        Assert.Equal(new CloakBrowserInstall(exe, Linux.Version, InstallSource.VendorCache), Find(Linux));
    }

    [Fact]
    public void Finds_the_executable_inside_a_macos_bundle()
    {
        var exe = CreateExecutable(VendorDir, $"chromium-{Mac.Version}", "Chromium.app", "Contents", "MacOS", "Chromium");

        Assert.Equal(exe, Find(Mac)?.Path);
    }

    [Fact]
    public void Vendor_cache_env_var_relocates_the_lookup()
    {
        var relocated = Path.Combine(_root, "elsewhere");
        _env["CLOAKBROWSER_CACHE_DIR"] = relocated;
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        var exe = CreateExecutable(relocated, "chromium-146.0.7680.177.3", "chrome");

        Assert.Equal(exe, Find(Linux)?.Path);
    }

    [Fact]
    public void Newest_complete_vendor_build_wins()
    {
        CreateExecutable(VendorDir, "chromium-146.0.7680.177.5", "chrome");
        // Newer than .5 numerically, older as a string.
        var newest = CreateExecutable(VendorDir, "chromium-146.0.7680.177.10", "chrome");
        // A Pro build and an incomplete newer build are skipped.
        CreateExecutable(VendorDir, "chromium-152.0.7977.82.1-pro", "chrome");
        Directory.CreateDirectory(Path.Combine(VendorDir, "chromium-160.0.0.0"));

        Assert.Equal(new CloakBrowserInstall(newest, "146.0.7680.177.10", InstallSource.VendorCache), Find(Linux));
    }

    [Fact]
    public void WebReaper_cache_wins_over_the_vendor_cache()
    {
        CreateExecutable(VendorDir, "chromium-999.0.0.0.0", "chrome");
        var exe = CreateExecutable(CacheRoot, Linux.Version, "chrome");

        Assert.Equal(new CloakBrowserInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
    }

    [Fact]
    public void A_pinned_version_matches_only_that_build()
    {
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        Assert.Null(Find(Linux, pinned: "146.0.7680.177.4"));

        var exe = CreateExecutable(VendorDir, "chromium-146.0.7680.177.4", "chrome");
        Assert.Equal(
            new CloakBrowserInstall(exe, "146.0.7680.177.4", InstallSource.VendorCache),
            Find(Linux, pinned: "146.0.7680.177.4"));
    }

    [Fact]
    public void Binary_path_env_var_wins_over_every_cache()
    {
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        var custom = CreateExecutable(_root, "custom", "my-chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = custom;

        Assert.Equal(new CloakBrowserInstall(custom, null, InstallSource.BinaryPathEnvVar), Find(Linux));
    }

    [Fact]
    public void Binary_path_env_var_naming_a_missing_file_is_an_error()
    {
        // Not a fall-through: a typo must not silently pick a different binary.
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = Path.Combine(_root, "missing");

        var ex = Assert.Throws<FileNotFoundException>(() => Find(Linux));
        Assert.Contains("CLOAKBROWSER_BINARY_PATH", ex.Message);
    }

    [Fact]
    public void Without_a_build_for_the_platform_only_the_override_counts()
    {
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        Assert.Null(Find(build: null));

        var custom = CreateExecutable(_root, "custom", "chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = custom;
        Assert.Equal(custom, Find(build: null)?.Path);
    }

    [Fact]
    public void A_file_without_the_execute_bit_is_not_an_install()
    {
        if (OperatingSystem.IsWindows()) return; // no execute bit to check
        var exe = CreateExecutable(CacheRoot, Linux.Version, "chrome");
        File.SetUnixFileMode(exe, Regular);

        Assert.Null(Find(Linux));
    }

    // ----- platforms, pins, versions -----

    [Theory]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx", Architecture.X64, "osx-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("win", Architecture.Arm64, "win-arm64")]
    [InlineData("win", Architecture.X86, "win-x86")]
    [InlineData(null, Architecture.X64, null)]
    [InlineData("linux", Architecture.S390x, null)]
    public void PlatformKey_maps_os_and_architecture_to_a_rid(string? os, Architecture arch, string? expected) =>
        Assert.Equal(expected, CloakBrowserBuild.PlatformKey(os, arch));

    [Fact]
    public void CurrentPlatform_names_the_test_machine() =>
        Assert.NotNull(CloakBrowserBuild.CurrentPlatform());

    // The vendor wrappers' per-platform pins (PLATFORM_CHROMIUM_VERSIONS in the
    // npm wrapper's config.ts): not every release carries every platform, so
    // macOS and linux-arm64 trail linux-x64 and windows-x64.
    [Theory]
    [InlineData("linux-x64", "146.0.7680.177.5", "cloakbrowser-linux-x64.tar.gz", "chrome")]
    [InlineData("linux-arm64", "146.0.7680.177.3", "cloakbrowser-linux-arm64.tar.gz", "chrome")]
    [InlineData("osx-arm64", "145.0.7632.109.2", "cloakbrowser-darwin-arm64.tar.gz", "Chromium.app/Contents/MacOS/Chromium")]
    [InlineData("osx-x64", "145.0.7632.109.2", "cloakbrowser-darwin-x64.tar.gz", "Chromium.app/Contents/MacOS/Chromium")]
    [InlineData("win-x64", "146.0.7680.177.5", "cloakbrowser-windows-x64.zip", "chrome.exe")]
    public void Each_platform_pins_the_build_the_vendor_wrappers_pin(
        string platform, string version, string asset, string executable)
    {
        var build = CloakBrowserBuild.For(platform);

        Assert.NotNull(build);
        Assert.Equal((version, asset, executable), (build.Version, build.Asset, build.Executable));
    }

    [Fact]
    public void Platforms_without_an_upstream_build_have_no_row()
    {
        Assert.Equal(CloakBrowserBuild.All.Length, CloakBrowserBuild.All.Select(b => b.Platform).Distinct().Count());
        Assert.Null(CloakBrowserBuild.For("win-arm64"));
        Assert.Null(CloakBrowserBuild.For("linux-x86"));
        Assert.Null(CloakBrowserBuild.For(null));
    }

    [Fact]
    public void DefaultVersion_is_the_linux_x64_and_windows_x64_pin()
    {
        Assert.Equal(CloakBrowserInstaller.DefaultVersion, "chromium-v" + Linux.Version);
        Assert.Equal(CloakBrowserInstaller.DefaultVersion, "chromium-v" + Windows.Version);
    }

    [Theory]
    [InlineData("chromium-v146.0.7680.177.5", "146.0.7680.177.5")] // the release tag, the form 11.x documented
    [InlineData("146.0.7680.177.5", "146.0.7680.177.5")] // the bare build, the vendor's and the CLI's form
    [InlineData(" 145.0.7632.109.2 ", "145.0.7632.109.2")]
    [InlineData("152.0.7977.82.1-pro", "152.0.7977.82.1-pro")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeVersion_accepts_a_release_tag_or_a_bare_build(string? version, string? expected) =>
        Assert.Equal(expected, CloakBrowserInstaller.NormalizeVersion(version));

    [Theory]
    [InlineData("../../etc")]
    [InlineData("chromium-v../../etc")]
    [InlineData("146..0")]
    [InlineData("146/0")]
    [InlineData("146\\0")]
    [InlineData("146 0")]
    [InlineData("v146.0.7680.177.5")]
    [InlineData("chromium-v")]
    [InlineData("latest")]
    public void NormalizeVersion_rejects_anything_but_a_build(string version) =>
        Assert.Throws<ArgumentException>(() => CloakBrowserInstaller.NormalizeVersion(version));

    // ----- checksum manifest -----

    // The real chromium-v146.0.7680.177.5 SHA256SUMS, version header included.
    private const string Manifest =
        "version=146.0.7680.177.5\n" +
        "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e  cloakbrowser-linux-x64.tar.gz\n" +
        "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe  cloakbrowser-windows-x64.zip\n";

    [Fact]
    public void ParseChecksum_returns_the_hash_listed_for_the_file()
    {
        Assert.Equal(
            "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e",
            CloakBrowserInstaller.ParseChecksum(Manifest, "cloakbrowser-linux-x64.tar.gz"));
        Assert.Equal(
            "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe",
            CloakBrowserInstaller.ParseChecksum(Manifest, "cloakbrowser-windows-x64.zip"));
    }

    [Fact]
    public void ParseChecksum_returns_null_for_an_unlisted_file() =>
        Assert.Null(CloakBrowserInstaller.ParseChecksum(Manifest, "cloakbrowser-darwin-arm64.tar.gz"));

    [Fact]
    public void ParseChecksum_tolerates_crlf_blank_lines_uppercase_and_the_binary_marker()
    {
        var manifest = "\r\n4A12BCDE95FA1BB1BEEF2B41AB5E5C27C36BE78E3BE3D0DAC8C64D705216670E *cloakbrowser-linux-x64.tar.gz\r\n\r\n";

        Assert.Equal(
            "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e",
            CloakBrowserInstaller.ParseChecksum(manifest, "cloakbrowser-linux-x64.tar.gz"));
    }

    [Theory]
    [InlineData("4a12bcde  cloakbrowser-linux-x64.tar.gz")] // too short
    [InlineData("zz12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e  cloakbrowser-linux-x64.tar.gz")] // not hex
    [InlineData("cloakbrowser-linux-x64.tar.gz")]
    public void ParseChecksum_ignores_malformed_lines(string line) =>
        Assert.Null(CloakBrowserInstaller.ParseChecksum(line, "cloakbrowser-linux-x64.tar.gz"));

    // ----- download + verify + unpack -----

    private sealed class StubRelease : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void Serve(string url, byte[] body) => _files[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            return Task.FromResult(_files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static string ReleaseUrl(string version, string file) =>
        $"https://github.com/CloakHQ/CloakBrowser/releases/download/chromium-v{version}/{file}";

    private static string ManifestUrl(string version) => ReleaseUrl(version, "SHA256SUMS");

    // A release of `build` at `version` whose SHA256SUMS lists `archive`'s
    // real hash, unless `listedHash` overrides it.
    private static StubRelease ReleaseOf(CloakBrowserBuild build, byte[] archive, string? version = null, string? listedHash = null)
    {
        version ??= build.Version;
        var hash = listedHash ?? Convert.ToHexStringLower(SHA256.HashData(archive));
        var release = new StubRelease();
        release.Serve(ManifestUrl(version), Encoding.UTF8.GetBytes($"version={version}\n{hash}  {build.Asset}\n"));
        release.Serve(ReleaseUrl(version, build.Asset), archive);
        return release;
    }

    private async Task<string> InstallAsync(
        StubRelease release, CloakBrowserBuild build, string? version = null, ILogger? logger = null)
    {
        using var http = new HttpClient(release, disposeHandler: false);
        return await CloakBrowserInstaller.InstallAsync(
            build, version ?? build.Version, CacheRoot, http, logger ?? NullLogger.Instance, CancellationToken.None);
    }

    private static PaxTarEntry TarFileEntry(string name, string content, UnixFileMode mode = Regular) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Mode = mode,
        };

    private static PaxTarEntry TarSymlink(string name, string target) =>
        new(TarEntryType.SymbolicLink, name) { LinkName = target };

    private static byte[] TarGz(params TarEntry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries) tar.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task Install_verifies_then_unpacks_the_build_into_the_webreaper_cache()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "browser", Executable), TarFileEntry("libEGL.so", "lib")));
        var logger = new ListLogger();

        var exe = await InstallAsync(release, Linux, logger: logger);

        var buildDir = Path.Combine(CacheRoot, Linux.Version);
        Assert.Equal(Path.Combine(buildDir, "chrome"), exe);
        Assert.Equal("browser", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(buildDir, "libEGL.so")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        // The manifest is fetched before the archive; only the build is left behind.
        Assert.Equal(new[] { ManifestUrl(Linux.Version), ReleaseUrl(Linux.Version, Linux.Asset) }, release.Requests);
        Assert.Equal(new[] { buildDir }, Directory.GetFileSystemEntries(CacheRoot));
        Assert.Contains(logger.Entries, e => e.Message.Contains("SHA-256 verified"));
        // A later lookup finds it instead of downloading again.
        Assert.Equal(new CloakBrowserInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
    }

    [Fact]
    public async Task Install_of_a_pinned_version_uses_that_release()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "older", Executable)), version: "146.0.7680.177.4");

        var exe = await InstallAsync(release, Linux, version: "146.0.7680.177.4");

        Assert.Equal(Path.Combine(CacheRoot, "146.0.7680.177.4", "chrome"), exe);
        Assert.All(release.Requests, url => Assert.Contains("/chromium-v146.0.7680.177.4/", url));
    }

    [Fact]
    public async Task Install_unpacks_a_zip_build()
    {
        var release = ReleaseOf(Windows, Zip(("chrome.exe", "MZ"), ("locales/en-US.pak", "pak")));

        var exe = await InstallAsync(release, Windows);

        Assert.Equal(Path.Combine(CacheRoot, Windows.Version, "chrome.exe"), exe);
        Assert.True(File.Exists(Path.Combine(CacheRoot, Windows.Version, "locales", "en-US.pak")));
    }

    [Fact]
    public async Task Install_keeps_the_symlinks_inside_a_macos_bundle()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges there
        const string framework = "Chromium.app/Contents/Frameworks/Chromium Framework.framework";
        var release = ReleaseOf(Mac, TarGz(
            TarFileEntry("Chromium.app/Contents/MacOS/Chromium", "browser", Executable),
            TarFileEntry($"{framework}/Versions/A/Resources/en.pak", "pak"),
            TarSymlink($"{framework}/Versions/Current", "A"),
            TarSymlink($"{framework}/Resources", "Versions/Current/Resources")));

        var exe = await InstallAsync(release, Mac);

        var buildDir = Path.Combine(CacheRoot, Mac.Version);
        Assert.Equal(Path.Combine(buildDir, "Chromium.app", "Contents", "MacOS", "Chromium"), exe);
        var link = Path.Combine(buildDir, "Chromium.app", "Contents", "Frameworks", "Chromium Framework.framework", "Resources");
        Assert.Equal("Versions/Current/Resources", new DirectoryInfo(link).LinkTarget);
        Assert.Equal("pak", File.ReadAllText(Path.Combine(link, "en.pak")));
    }

    [Fact]
    public async Task Release_without_this_platform_fails_before_the_download()
    {
        var release = new StubRelease();
        release.Serve(ManifestUrl("146.0.7680.177.4"), Encoding.UTF8.GetBytes(
            "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe  cloakbrowser-windows-x64.zip\n"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallAsync(release, Linux, version: "146.0.7680.177.4"));

        Assert.Contains(Linux.Asset, ex.Message);
        Assert.Contains($"Unset CloakBrowserOptions.Version to install the pinned {Linux.Version}", ex.Message);
        Assert.Equal(new[] { ManifestUrl("146.0.7680.177.4") }, release.Requests);
        Assert.False(Directory.Exists(Path.Combine(CacheRoot, "146.0.7680.177.4")));
    }

    [Fact]
    public async Task Missing_release_names_the_manifest_url()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => InstallAsync(new StubRelease(), Linux, version: "1.0.0.0"));

        Assert.Contains(ManifestUrl("1.0.0.0"), ex.Message);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Checksum_mismatch_installs_nothing()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "tampered", Executable)), listedHash: new string('0', 64));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InstallAsync(release, Linux));

        Assert.Contains("checksum mismatch", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task Archive_without_the_executable_installs_nothing()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("README", "not a browser")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InstallAsync(release, Linux));

        Assert.Contains(Linux.Executable, ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task A_stale_partial_build_dir_is_replaced()
    {
        // An interrupted older install: the version dir exists, the executable doesn't.
        var buildDir = Path.Combine(CacheRoot, Linux.Version);
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "leftover"), "x");
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "fresh", Executable)));

        var exe = await InstallAsync(release, Linux);

        Assert.Equal("fresh", File.ReadAllText(exe));
        Assert.False(File.Exists(Path.Combine(buildDir, "leftover")));
    }

    [Fact]
    public async Task Leftovers_of_a_killed_install_are_swept_but_a_live_one_is_kept()
    {
        Directory.CreateDirectory(CacheRoot);
        var deadArchive = Path.Combine(CacheRoot, ".partial-dead-cloakbrowser-linux-x64.tar.gz");
        var deadScratch = Path.Combine(CacheRoot, ".partial-dead");
        var liveArchive = Path.Combine(CacheRoot, ".partial-live-cloakbrowser-linux-x64.tar.gz");
        File.WriteAllText(deadArchive, "half a download");
        Directory.CreateDirectory(deadScratch);
        File.WriteAllText(liveArchive, "still downloading");
        var twoHoursAgo = DateTime.UtcNow - TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(deadArchive, twoHoursAgo);
        Directory.SetLastWriteTimeUtc(deadScratch, twoHoursAgo);

        await InstallAsync(ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "browser", Executable))), Linux);

        Assert.False(File.Exists(deadArchive));
        Assert.False(Directory.Exists(deadScratch));
        Assert.True(File.Exists(liveArchive));
    }

    // ----- EnsureInstalledAsync: lookup, policy, download -----

    private Task<string> EnsureAsync(
        CloakBrowserOptions options, StubRelease? release = null, string? platform = "linux-x64", ILogger? logger = null) =>
        CloakBrowserInstaller.EnsureInstalledAsync(
            options,
            logger ?? NullLogger.Instance,
            platform,
            WebReaperHome,
            UserHome,
            name => _env.GetValueOrDefault(name),
            () => new HttpClient(release ?? new StubRelease(), disposeHandler: false),
            CancellationToken.None);

    [Fact]
    public async Task An_existing_install_is_used_without_touching_the_network()
    {
        var exe = CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        var release = new StubRelease();

        Assert.Equal(exe, await EnsureAsync(new CloakBrowserOptions(), release));
        Assert.Empty(release.Requests);
    }

    [Fact]
    public async Task Nothing_installed_downloads_the_platforms_pinned_build_once()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "browser", Executable)));
        var options = new CloakBrowserOptions { AutoInstall = AutoInstallPolicy.NoPromptYes };

        var first = await EnsureAsync(options, release);
        var second = await EnsureAsync(options, release);

        Assert.Equal(Path.Combine(CacheRoot, Linux.Version, "chrome"), first);
        Assert.Equal(first, second);
        // The second call found the install; only the first touched the network.
        Assert.Equal(2, release.Requests.Count);
    }

    [Fact]
    public async Task macOS_installs_its_own_pinned_build()
    {
        // Before the per-platform pins this threw PlatformNotSupportedException.
        var release = ReleaseOf(Mac, TarGz(TarFileEntry("Chromium.app/Contents/MacOS/Chromium", "browser", Executable)));

        var exe = await EnsureAsync(
            new CloakBrowserOptions { AutoInstall = AutoInstallPolicy.NoPromptYes }, release, platform: "osx-arm64");

        Assert.Equal(Path.Combine(CacheRoot, Mac.Version, "Chromium.app", "Contents", "MacOS", "Chromium"), exe);
        Assert.Equal(new[] { ManifestUrl(Mac.Version), ReleaseUrl(Mac.Version, Mac.Asset) }, release.Requests);
    }

    [Fact]
    public async Task Version_option_installs_that_build_from_its_release()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "older", Executable)), version: "146.0.7680.177.4");

        var exe = await EnsureAsync(
            new CloakBrowserOptions { Version = "chromium-v146.0.7680.177.4", AutoInstall = AutoInstallPolicy.NoPromptYes },
            release);

        Assert.Equal(Path.Combine(CacheRoot, "146.0.7680.177.4", "chrome"), exe);
        Assert.All(release.Requests, url => Assert.Contains("/chromium-v146.0.7680.177.4/", url));
    }

    [Fact]
    public async Task An_invalid_Version_is_rejected_before_any_lookup()
    {
        var release = new StubRelease();

        await Assert.ThrowsAsync<ArgumentException>(
            () => EnsureAsync(new CloakBrowserOptions { Version = "../../etc" }, release));
        Assert.Empty(release.Requests);
    }

    [Theory]
    [InlineData(AutoInstallPolicy.PromptLogger, true)]
    [InlineData(AutoInstallPolicy.NoPromptYes, false)]
    public async Task Only_PromptLogger_logs_the_license_line(AutoInstallPolicy policy, bool logsLicense)
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "browser", Executable)));
        var logger = new ListLogger();

        await EnsureAsync(new CloakBrowserOptions { AutoInstall = policy }, release, logger: logger);

        Assert.Equal(
            logsLicense,
            logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(CloakBrowserInstaller.LicenseUrl)));
    }

    [Fact]
    public async Task AutoInstall_Disabled_fails_without_downloading()
    {
        var release = new StubRelease();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureAsync(new CloakBrowserOptions { AutoInstall = AutoInstallPolicy.Disabled }, release));

        Assert.Contains("AutoInstall is Disabled", ex.Message);
        Assert.Contains(Path.Combine(CacheRoot, Linux.Version), ex.Message);
        Assert.Contains(VendorDir, ex.Message);
        Assert.Empty(release.Requests);
    }

    [Fact]
    public async Task AutoInstall_Disabled_still_reuses_an_existing_install()
    {
        var exe = CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");

        Assert.Equal(exe, await EnsureAsync(new CloakBrowserOptions { AutoInstall = AutoInstallPolicy.Disabled }));
    }

    [Fact]
    public async Task An_unpublished_platform_fails_fast_naming_the_published_builds()
    {
        var release = new StubRelease();

        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => EnsureAsync(new CloakBrowserOptions(), release, platform: "win-arm64"));

        Assert.Contains("win-arm64", ex.Message);
        Assert.Contains("osx-arm64", ex.Message);
        Assert.Contains("CLOAKBROWSER_BINARY_PATH", ex.Message);
        Assert.Empty(release.Requests);
    }

    [Fact]
    public async Task ExecutablePath_option_wins_over_the_env_override()
    {
        var mine = CreateExecutable(_root, "mine", "chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = CreateExecutable(_root, "env", "chrome");

        Assert.Equal(mine, await EnsureAsync(new CloakBrowserOptions { ExecutablePath = mine }));
    }

    [Fact]
    public async Task A_missing_ExecutablePath_is_an_error() =>
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => EnsureAsync(new CloakBrowserOptions { ExecutablePath = Path.Combine(_root, "missing") }));
}
