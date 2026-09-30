namespace WebReaper.Stealth.CloakBrowser.Tests;

/// <summary>
/// The CloakBrowser launch flags (ADR-0054 amendment). The flags add no
/// stealth; each one guards against a startup stall or a dialog.
/// </summary>
public sealed class CloakBrowserLauncherTests
{
    [Fact]
    public void Launch_flags_keep_macos_off_the_login_keychain() =>
        // Without it the macOS build stalls on the login keychain and never
        // publishes its CDP endpoint (#264: none within 15 s, 2 s with it).
        Assert.Contains("--use-mock-keychain", CloakBrowserLauncher.RecommendedArgs);
}
