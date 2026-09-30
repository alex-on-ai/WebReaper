using WebReaper.Cli.Commands;
using WebReaper.Cli.Stealth;

namespace WebReaper.Cli.Tests;

/// <summary>
/// #264: on a platform CloakBrowser publishes no build for (Windows on ARM,
/// 32-bit), install and path fail with a message that says so and points at
/// the two ways to still use stealth.
/// </summary>
public class StealthCommandTests
{
    [Fact]
    public void No_build_message_names_the_platform_the_builds_and_the_ways_out()
    {
        var message = StealthCommand.NoBuildMessage(KnownStealthBackends.Find("cloakbrowser")!, "win-arm64");

        Assert.Contains("no build for win-arm64", message);
        Assert.Contains("linux-x64, linux-arm64, osx-arm64, osx-x64, win-x64", message);
        Assert.Contains("CLOAKBROWSER_BINARY_PATH", message);
        Assert.Contains("--browser-cdp-url", message);
    }

    [Fact]
    public void No_build_message_covers_an_unrecognised_platform() =>
        Assert.Contains(
            "no build for this platform",
            StealthCommand.NoBuildMessage(KnownStealthBackends.Find("cloakbrowser")!, platform: null));
}
