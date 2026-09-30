using WebReaper.Cli.Stealth;

namespace WebReaper.Cli.Tests;

/// <summary>
/// ADR-0055. The curated static registry of stealth backends. CloakBrowser
/// is the v10.0.0 inaugural entry. Adding a backend = a row here +
/// (optionally) the matching library satellite; these tests pin the
/// shape so a missed field surfaces at PR-time.
/// </summary>
public class KnownStealthBackendsTests
{
    [Fact]
    public void Registry_has_at_least_one_backend()
    {
        Assert.NotEmpty(KnownStealthBackends.All);
    }

    [Fact]
    public void Cloakbrowser_is_registered()
    {
        var b = KnownStealthBackends.Find("cloakbrowser");
        Assert.NotNull(b);
        Assert.Equal("CloakBrowser", b!.DisplayName);
        Assert.Contains("CloakHQ", b.LicenseUrl);
        Assert.NotEmpty(b.Builds);
        Assert.NotEmpty(b.LaunchArgs);
        Assert.Equal("CLOAKBROWSER_BINARY_PATH", b.BinaryPathEnvVar);
        Assert.NotNull(b.VendorCache);
    }

    [Fact]
    public void Cloakbrowser_release_urls_use_the_chromium_tag_scheme()
    {
        // #264: upstream tags releases `chromium-v<build>`; the old
        // `v0.3.30/cloakbrowser-<rid>.tar.gz` URL 404s.
        var b = KnownStealthBackends.Find("cloakbrowser")!;

        Assert.Equal(
            "https://github.com/CloakHQ/CloakBrowser/releases/download/chromium-v146.0.7680.177.5/cloakbrowser-linux-x64.tar.gz",
            b.ReleaseUrl("146.0.7680.177.5", "cloakbrowser-linux-x64.tar.gz"));
        Assert.Equal(
            "https://github.com/CloakHQ/CloakBrowser/releases/download/chromium-v146.0.7680.177.5/SHA256SUMS",
            b.ReleaseUrl("146.0.7680.177.5", b.ChecksumFile));
    }

    [Fact]
    public void Cloakbrowser_has_a_build_for_each_platform_upstream_publishes()
    {
        var b = KnownStealthBackends.Find("cloakbrowser")!;

        Assert.Equal(
            new[] { "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-x64" },
            b.Builds.Select(x => x.Platform).Order(StringComparer.Ordinal));
        Assert.Equal("cloakbrowser-darwin-arm64.tar.gz", b.BuildFor("osx-arm64")!.Asset);
        Assert.Equal("cloakbrowser-windows-x64.zip", b.BuildFor("win-x64")!.Asset);
    }

    [Fact]
    public void Cloakbrowser_launches_off_the_macos_keychain()
    {
        // Without it the macOS build stalls at startup and never publishes
        // its CDP endpoint, so the stealth rung times out.
        Assert.Contains("--use-mock-keychain", KnownStealthBackends.Find("cloakbrowser")!.LaunchArgs);
    }

    [Theory]
    [InlineData("win-arm64")]
    [InlineData("win-x86")]
    [InlineData("linux-arm")]
    [InlineData(null)]
    public void Cloakbrowser_has_no_build_for_platforms_upstream_skips(string? platform)
    {
        Assert.Null(KnownStealthBackends.Find("cloakbrowser")!.BuildFor(platform));
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        Assert.NotNull(KnownStealthBackends.Find("CloakBrowser"));
        Assert.NotNull(KnownStealthBackends.Find("CLOAKBROWSER"));
        Assert.NotNull(KnownStealthBackends.Find("cloakbrowser"));
    }

    [Fact]
    public void Find_unknown_returns_null()
    {
        Assert.Null(KnownStealthBackends.Find("nonexistent"));
        Assert.Null(KnownStealthBackends.Find(""));
    }

    [Fact]
    public void Every_backend_has_required_metadata()
    {
        // Pins shape so a future PR adding a backend can't ship a partial row.
        foreach (var b in KnownStealthBackends.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(b.Name), $"Name missing: {b}");
            Assert.False(string.IsNullOrWhiteSpace(b.DisplayName), $"DisplayName missing: {b.Name}");
            Assert.False(string.IsNullOrWhiteSpace(b.LicenseUrl), $"LicenseUrl missing: {b.Name}");
            Assert.False(string.IsNullOrWhiteSpace(b.ChecksumFile), $"ChecksumFile missing: {b.Name}");
            Assert.Contains("{version}", b.ReleaseUrlPattern);
            Assert.Contains("{file}", b.ReleaseUrlPattern);
            Assert.NotEmpty(b.Builds);
            Assert.Equal(b.Builds.Count, b.Builds.Select(x => x.Platform).Distinct().Count());
        }
    }

    [Fact]
    public void Every_build_is_installable()
    {
        foreach (var b in KnownStealthBackends.All)
        foreach (var build in b.Builds)
        {
            var id = $"{b.Name}/{build.Platform}";
            Assert.True(StealthInstaller.IsValidVersion(build.Version), $"Version invalid: {id}");
            Assert.True(
                build.Asset.EndsWith(".tar.gz", StringComparison.Ordinal) || build.Asset.EndsWith(".zip", StringComparison.Ordinal),
                $"Asset is not a .tar.gz or .zip: {id}");
            Assert.False(string.IsNullOrWhiteSpace(build.Executable), $"Executable missing: {id}");
            Assert.False(build.Executable.StartsWith('/') || build.Executable.Contains(".."), $"Executable escapes the build dir: {id}");
            Assert.True(build.SizeMb > 0, $"SizeMb is non-positive: {id}");
        }
    }

    [Fact]
    public void Build_executables_match_their_platform()
    {
        foreach (var b in KnownStealthBackends.All)
        foreach (var build in b.Builds)
        {
            if (build.Platform.StartsWith("win-", StringComparison.Ordinal))
                Assert.EndsWith(".exe", build.Executable);
            else if (build.Platform.StartsWith("osx-", StringComparison.Ordinal))
                Assert.Contains(".app/Contents/MacOS/", build.Executable);
            else
                Assert.DoesNotContain('.', build.Executable);
        }
    }

    [Fact]
    public void Backend_names_are_lowercase_no_spaces()
    {
        // The Name is the CLI-token user types after `webreaper stealth install`.
        // Forbidding spaces and upper-case matches the ADR-0055 spec.
        foreach (var b in KnownStealthBackends.All)
        {
            Assert.Equal(b.Name, b.Name.ToLowerInvariant());
            Assert.DoesNotContain(' ', b.Name);
        }
    }
}
