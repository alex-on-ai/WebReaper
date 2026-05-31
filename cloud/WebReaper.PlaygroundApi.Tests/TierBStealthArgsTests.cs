using WebReaper.PlaygroundApi.Scraping;
using Xunit;

namespace WebReaper.PlaygroundApi.Tests;

// Pins the CloakBrowser stealth launch recipe (TierBScraper.BuildStealthArgs). The
// regression these guard: the launcher used to pass only sanity flags, so on a
// software-GL host the stealth browser ran with no fingerprint profile and -- headed
// -- no WebGL context (because --ignore-gpu-blocklist was missing), which is an
// instant Cloudflare bot tell. Diagnosed 2026-05-31: headless software-GL CloakBrowser
// reports gl:no-webgl; headed + --ignore-gpu-blocklist revives WebGL via SwiftShader.
public class TierBStealthArgsTests
{
    [Fact]
    public void Headed_software_gl_gets_the_webgl_blocklist_bypass()
    {
        var args = TierBScraper.BuildStealthArgs(headed: true, proxyServerArg: null, fingerprintPlatform: "windows", fingerprintSeed: 42069);
        // The flag that turns "no WebGL at all" into WebGL-via-SwiftShader under Xvfb.
        Assert.Contains("--ignore-gpu-blocklist", args);
        Assert.Contains("--window-size=1920,1080", args);
        Assert.DoesNotContain("--headless=new", args);
    }

    [Fact]
    public void Headless_stays_headless_and_skips_the_inert_blocklist_bypass()
    {
        var args = TierBScraper.BuildStealthArgs(headed: false, proxyServerArg: null, fingerprintPlatform: "windows", fingerprintSeed: 42069);
        Assert.Contains("--headless=new", args);
        // ignore-gpu-blocklist cannot revive WebGL in headless software-GL (verified
        // empirically), so the recipe does not add an inert flag there.
        Assert.DoesNotContain("--ignore-gpu-blocklist", args);
        Assert.DoesNotContain("--window-size=1920,1080", args);
    }

    [Fact]
    public void Always_carries_the_fingerprint_profile_so_the_binary_is_not_bare()
    {
        var args = TierBScraper.BuildStealthArgs(headed: true, proxyServerArg: null, fingerprintPlatform: "windows", fingerprintSeed: 42069);
        Assert.Contains("--fingerprint=42069", args);
        Assert.Contains("--fingerprint-platform=windows", args);
        // The vendor's hardened sanity flags + the root-container --no-sandbox survive.
        Assert.Contains("--no-sandbox", args);
        Assert.Contains("--disable-dev-shm-usage", args);
    }

    [Fact]
    public void Honours_a_non_default_fingerprint_platform()
    {
        var args = TierBScraper.BuildStealthArgs(headed: false, proxyServerArg: null, fingerprintPlatform: "macos", fingerprintSeed: 7);
        Assert.Contains("--fingerprint-platform=macos", args);
    }

    [Fact]
    public void Threads_proxy_and_geoip_alignment_when_set()
    {
        var args = TierBScraper.BuildStealthArgs(headed: true, proxyServerArg: "http://gw.example.com:8080",
            fingerprintPlatform: "windows", fingerprintSeed: 1, timezone: "America/New_York", locale: "en-US");
        Assert.Contains("--proxy-server=http://gw.example.com:8080", args);
        Assert.Contains("--fingerprint-timezone=America/New_York", args);
        Assert.Contains("--lang=en-US", args);
        Assert.Contains("--fingerprint-locale=en-US", args);
    }

    [Fact]
    public void Omits_proxy_and_geoip_flags_when_unset()
    {
        var args = TierBScraper.BuildStealthArgs(headed: false, proxyServerArg: null, fingerprintPlatform: "windows", fingerprintSeed: 1);
        Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server", System.StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--fingerprint-timezone", System.StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--lang", System.StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--fingerprint-locale", System.StringComparison.Ordinal));
    }
}
