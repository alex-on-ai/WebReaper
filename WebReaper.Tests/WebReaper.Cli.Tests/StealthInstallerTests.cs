using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WebReaper.Cli.Stealth;

namespace WebReaper.Cli.Tests;

/// <summary>
/// #264: the CLI's stealth installer. Existing-install discovery (the
/// binary-path override, the CLI cache, the vendor wrapper's cache) runs
/// against temp directories; the download path runs against a stub
/// <see cref="HttpMessageHandler"/> serving an in-memory release. No test
/// touches the network or the real home directory.
/// </summary>
public sealed class StealthInstallerTests : IDisposable
{
    private static readonly StealthBackend Cloak = KnownStealthBackends.Find("cloakbrowser")!;

    // Builds are looked up by name, not by the test machine's platform, so
    // every OS exercises the same layouts.
    private static readonly StealthBuild Linux = Cloak.BuildFor("linux-x64")!;
    private static readonly StealthBuild Mac = Cloak.BuildFor("osx-arm64")!;
    private static readonly StealthBuild Windows = Cloak.BuildFor("win-x64")!;

    private const UnixFileMode Regular =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Executable =
        Regular | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wr-stealth-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _env = new();

    private string WebReaperHome => Path.Combine(_root, "webreaper");
    private string UserHome => Path.Combine(_root, "home");
    private string CacheRoot => StealthInstaller.CacheRoot(Cloak, WebReaperHome);
    private string VendorDir => Path.Combine(UserHome, ".cloakbrowser");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ----- existing-install discovery -----

    private StealthInstall? Find(StealthBuild? build, string? pinned = null) =>
        StealthInstaller.FindInstalled(Cloak, build, pinned, WebReaperHome, UserHome, name => _env.GetValueOrDefault(name));

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
    public void Finds_the_pinned_build_in_the_cli_cache()
    {
        var exe = CreateExecutable(CacheRoot, Linux.Version, "chrome");

        Assert.Equal(new StealthInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
    }

    [Fact]
    public void Reuses_a_build_from_the_vendor_wrapper_cache()
    {
        // The #264 reporter's layout: `npm i -g cloakbrowser` unpacked the
        // browser to ~/.cloakbrowser/chromium-<build>/chrome.
        var exe = CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");

        Assert.Equal(new StealthInstall(exe, Linux.Version, InstallSource.VendorCache), Find(Linux));
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

        Assert.Equal(new StealthInstall(newest, "146.0.7680.177.10", InstallSource.VendorCache), Find(Linux));
    }

    [Fact]
    public void Cli_cache_wins_over_the_vendor_cache()
    {
        CreateExecutable(VendorDir, "chromium-999.0.0.0.0", "chrome");
        var exe = CreateExecutable(CacheRoot, Linux.Version, "chrome");

        Assert.Equal(new StealthInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
    }

    [Fact]
    public void A_pinned_version_matches_only_that_build()
    {
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        Assert.Null(Find(Linux, pinned: "146.0.7680.177.4"));

        var exe = CreateExecutable(VendorDir, "chromium-146.0.7680.177.4", "chrome");
        Assert.Equal(new StealthInstall(exe, "146.0.7680.177.4", InstallSource.VendorCache), Find(Linux, pinned: "146.0.7680.177.4"));
    }

    [Fact]
    public void Binary_path_override_wins_over_every_cache()
    {
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        CreateExecutable(VendorDir, $"chromium-{Linux.Version}", "chrome");
        var custom = CreateExecutable(_root, "custom", "my-chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = custom;

        Assert.Equal(new StealthInstall(custom, null, InstallSource.EnvOverride), Find(Linux));
    }

    [Fact]
    public void Binary_path_override_naming_a_missing_file_is_an_error()
    {
        // Not a fall-through: a typo must not silently pick a different binary.
        CreateExecutable(CacheRoot, Linux.Version, "chrome");
        _env["CLOAKBROWSER_BINARY_PATH"] = Path.Combine(_root, "missing");

        var ex = Assert.Throws<CliException>(() => Find(Linux));
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

    // ----- platform + version -----

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
        Assert.Equal(expected, StealthInstaller.PlatformKey(os, arch));

    [Fact]
    public void CurrentPlatform_names_the_test_machine() =>
        Assert.NotNull(StealthInstaller.CurrentPlatform());

    [Theory]
    [InlineData("146.0.7680.177.5", true)]
    [InlineData("145.0.7632.109.2", true)]
    [InlineData("1.2.3-beta_1", true)]
    [InlineData("", false)]
    [InlineData("chromium-v146.0.7680.177.5", false)] // the release tag, not the build
    [InlineData("../../etc", false)]
    [InlineData("146..0", false)]
    [InlineData("146/0", false)]
    [InlineData("146\\0", false)]
    [InlineData("146 0", false)]
    public void IsValidVersion_accepts_only_build_numbers(string version, bool valid) =>
        Assert.Equal(valid, StealthInstaller.IsValidVersion(version));

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
            StealthInstaller.ParseChecksum(Manifest, "cloakbrowser-linux-x64.tar.gz"));
        Assert.Equal(
            "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe",
            StealthInstaller.ParseChecksum(Manifest, "cloakbrowser-windows-x64.zip"));
    }

    [Fact]
    public void ParseChecksum_returns_null_for_an_unlisted_file() =>
        Assert.Null(StealthInstaller.ParseChecksum(Manifest, "cloakbrowser-darwin-arm64.tar.gz"));

    [Fact]
    public void ParseChecksum_tolerates_crlf_blank_lines_uppercase_and_the_binary_marker()
    {
        var manifest = "\r\n4A12BCDE95FA1BB1BEEF2B41AB5E5C27C36BE78E3BE3D0DAC8C64D705216670E *cloakbrowser-linux-x64.tar.gz\r\n\r\n";

        Assert.Equal(
            "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e",
            StealthInstaller.ParseChecksum(manifest, "cloakbrowser-linux-x64.tar.gz"));
    }

    [Theory]
    [InlineData("4a12bcde  cloakbrowser-linux-x64.tar.gz")] // too short
    [InlineData("zz12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e  cloakbrowser-linux-x64.tar.gz")] // not hex
    [InlineData("cloakbrowser-linux-x64.tar.gz")]
    public void ParseChecksum_ignores_malformed_lines(string line) =>
        Assert.Null(StealthInstaller.ParseChecksum(line, "cloakbrowser-linux-x64.tar.gz"));

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

    private static string ManifestUrl(string version) => Cloak.ReleaseUrl(version, Cloak.ChecksumFile);

    // A release of `build` at `version` whose SHA256SUMS lists `archive`'s
    // real hash, unless `listedHash` overrides it.
    private static StubRelease ReleaseOf(StealthBuild build, byte[] archive, string? version = null, string? listedHash = null)
    {
        version ??= build.Version;
        var hash = listedHash ?? Convert.ToHexStringLower(SHA256.HashData(archive));
        var release = new StubRelease();
        release.Serve(ManifestUrl(version), Encoding.UTF8.GetBytes($"version={version}\n{hash}  {build.Asset}\n"));
        release.Serve(Cloak.ReleaseUrl(version, build.Asset), archive);
        return release;
    }

    private Task<string> InstallAsync(StubRelease release, StealthBuild build, string? version = null, TextWriter? log = null) =>
        StealthInstaller.InstallAsync(
            Cloak, build, version ?? build.Version, CacheRoot, new HttpClient(release), log ?? TextWriter.Null);

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
    public async Task Install_verifies_then_unpacks_the_build_into_the_cli_cache()
    {
        var release = ReleaseOf(Linux, TarGz(TarFileEntry("chrome", "browser", Executable), TarFileEntry("libEGL.so", "lib")));
        var log = new StringWriter();

        var exe = await InstallAsync(release, Linux, log: log);

        var buildDir = Path.Combine(CacheRoot, Linux.Version);
        Assert.Equal(Path.Combine(buildDir, "chrome"), exe);
        Assert.Equal("browser", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(buildDir, "libEGL.so")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        // The manifest is fetched before the archive; only the build is left behind.
        Assert.Equal(new[] { ManifestUrl(Linux.Version), Cloak.ReleaseUrl(Linux.Version, Linux.Asset) }, release.Requests);
        Assert.Equal(new[] { buildDir }, Directory.GetFileSystemEntries(CacheRoot));
        Assert.Contains("SHA-256 verified", log.ToString());
        // A later lookup finds it instead of downloading again.
        Assert.Equal(new StealthInstall(exe, Linux.Version, InstallSource.WebReaperCache), Find(Linux));
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
        Assert.Contains($"Omit --version to install the pinned {Linux.Version}", ex.Message);
        Assert.Equal(new[] { ManifestUrl("146.0.7680.177.4") }, release.Requests);
        Assert.False(Directory.Exists(Path.Combine(CacheRoot, "146.0.7680.177.4")));
    }

    [Fact]
    public async Task Missing_release_names_the_manifest_url()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallAsync(new StubRelease(), Linux, version: "1.0.0.0"));

        Assert.Contains(ManifestUrl("1.0.0.0"), ex.Message);
        Assert.Contains("HTTP 404", ex.Message);
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
}
