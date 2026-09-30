using WebReaper.Cli.Stealth;

namespace WebReaper.Cli.Tests;

/// <summary>
/// ADR-0055 amendment (fingerprint profile). The flags the scrape's stealth
/// rung launches CloakBrowser with: the vendor wrappers' fingerprint persona
/// per host OS plus a per-launch seed, with Chromium's sandbox kept on except
/// where it cannot start (root on Linux). <see cref="StealthBackend.LaunchArgsFor(string?, int, bool)"/>
/// is pure so the recipe is pinned here; ScrapeCommand only supplies the RID
/// and whether the process runs as root.
/// </summary>
public class StealthLaunchArgsTests
{
    private static readonly StealthBackend Cloak = KnownStealthBackends.Find("cloakbrowser")!;

    public static TheoryData<string?> Platforms =>
        ["osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "win-x64", null];

    [Theory]
    [InlineData("osx-arm64", "macos")]
    [InlineData("osx-x64", "macos")]
    [InlineData("linux-x64", "windows")]
    [InlineData("linux-arm64", "windows")]
    [InlineData("win-x64", "windows")]
    public void Cloakbrowser_presents_the_vendor_persona_for_the_host_os(string platform, string persona)
    {
        var args = Cloak.LaunchArgsFor(platform, 42069, privileged: false);

        Assert.Contains("--fingerprint=42069", args);
        Assert.Contains($"--fingerprint-platform={persona}", args);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Cloakbrowser_keeps_the_sandbox_for_a_normal_user(string? platform)
    {
        Assert.DoesNotContain("--no-sandbox", Cloak.LaunchArgsFor(platform, 42069, privileged: false));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void Cloakbrowser_drops_the_sandbox_for_root_on_linux(string platform)
    {
        // Chromium exits at startup as root unless the sandbox is off
        // (crbug.com/638180), e.g. in a container that runs as root.
        Assert.Contains("--no-sandbox", Cloak.LaunchArgsFor(platform, 42069, privileged: true));
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public void Cloakbrowser_keeps_the_sandbox_for_a_privileged_user_off_linux(string platform)
    {
        // The root refusal is Linux-only; on Windows "privileged" means an
        // elevated admin, which Chromium sandboxes normally.
        Assert.DoesNotContain("--no-sandbox", Cloak.LaunchArgsFor(platform, 42069, privileged: true));
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Cloakbrowser_starts_with_the_every_launch_flags(string? platform)
    {
        var args = Cloak.LaunchArgsFor(platform, 42069, privileged: true);

        Assert.Equal(Cloak.LaunchArgs, args.Take(Cloak.LaunchArgs.Count));
        Assert.Contains("--use-mock-keychain", args);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Cloakbrowser_passes_one_seed_and_at_most_one_persona(string? platform)
    {
        var args = Cloak.LaunchArgsFor(platform, 42069, privileged: true);

        Assert.Single(args, a => a.StartsWith("--fingerprint=", StringComparison.Ordinal));
        Assert.True(args.Count(a => a.StartsWith("--fingerprint-platform=", StringComparison.Ordinal)) <= 1);
        Assert.Equal(args.Count, args.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("freebsd-x64")]
    public void Cloakbrowser_on_an_unknown_os_gets_the_seed_but_no_persona(string? platform)
    {
        // No persona flag: the binary presents as the platform it was built for.
        var args = Cloak.LaunchArgsFor(platform, 42069, privileged: false);

        Assert.Contains("--fingerprint=42069", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--fingerprint-platform=", StringComparison.Ordinal));
    }

    [Fact]
    public void Cloakbrowser_draws_a_fresh_seed_per_launch_in_the_vendor_range()
    {
        // The vendor's wrappers draw 10000 to 99999 per launch; so does the CLI.
        var seeds = Enumerable.Range(0, 20)
            .Select(_ => Cloak.LaunchArgsFor("osx-arm64", privileged: false)
                .Single(a => a.StartsWith("--fingerprint=", StringComparison.Ordinal)))
            .Select(a => int.Parse(a["--fingerprint=".Length..]))
            .ToList();

        Assert.All(seeds, s => Assert.InRange(s, 10_000, 99_999));
        Assert.True(seeds.Distinct().Count() > 1, "20 launches drew one seed");
    }

    [Fact]
    public void Os_flags_are_keyed_by_the_os_part_of_a_rid()
    {
        // StealthInstaller.PlatformKey builds "<os>-<cpu>" from these os parts;
        // a key like "macos" would never match.
        foreach (var b in KnownStealthBackends.All)
            Assert.All(b.LaunchArgsByOs.Keys, os => Assert.Contains(os, new[] { "linux", "osx", "win" }));
    }
}
