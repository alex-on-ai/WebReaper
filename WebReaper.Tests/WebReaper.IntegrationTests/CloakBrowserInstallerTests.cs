using WebReaper.Stealth.CloakBrowser;
using Xunit;

namespace WebReaper.IntegrationTests;

/// <summary>
/// Unit coverage for the one piece of custom parsing in the CloakBrowser
/// installer's download path: pulling an asset's SHA-256 out of a release
/// SHA256SUMS manifest. (The end-to-end download is the env-gated
/// <see cref="CloakBrowserSmokeTests"/>, which this fix makes functional on
/// linux-x64 / windows-x64.)
/// </summary>
public sealed class CloakBrowserInstallerTests
{
    // The real chromium-v146.0.7680.177.5 SHA256SUMS shape: "<hex>  <filename>".
    private const string Sums =
        "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e  cloakbrowser-linux-x64.tar.gz\n" +
        "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe  cloakbrowser-windows-x64.zip\n";

    [Fact]
    public void ParseExpectedSha_returns_the_hash_for_the_named_asset()
    {
        Assert.Equal(
            "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e",
            CloakBrowserInstaller.ParseExpectedSha(Sums, "cloakbrowser-linux-x64.tar.gz"));
        Assert.Equal(
            "b213795cb32c3169f766c74ce1d0275fc89d3df256de39c04da7fb4c23b7fdbe",
            CloakBrowserInstaller.ParseExpectedSha(Sums, "cloakbrowser-windows-x64.zip"));
    }

    [Fact]
    public void ParseExpectedSha_returns_null_when_the_asset_is_absent() =>
        Assert.Null(CloakBrowserInstaller.ParseExpectedSha(Sums, "cloakbrowser-osx-arm64.tar.gz"));

    [Fact]
    public void ParseExpectedSha_tolerates_blank_lines_and_crlf()
    {
        var sums = "\r\n" + Sums.Replace("\n", "\r\n") + "\r\n";
        Assert.Equal(
            "4a12bcde95fa1bb1beef2b41ab5e5c27c36be78e3be3d0dac8c64d705216670e",
            CloakBrowserInstaller.ParseExpectedSha(sums, "cloakbrowser-linux-x64.tar.gz"));
    }
}
