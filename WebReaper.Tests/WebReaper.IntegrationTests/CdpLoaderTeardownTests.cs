using WebReaper.Builders;
using WebReaper.Cdp;
using Xunit;

namespace WebReaper.IntegrationTests;

/// <summary>
/// ADR-0058: the launch-and-connect <c>WithCdpPageLoader(CdpLaunchOptions)</c>
/// overload must register its lazily-spawned browser for engine teardown, the
/// same way <c>WithCloakBrowser</c> registers its subprocess. The engine does not
/// dispose page-load transports itself (<c>EscalatingPageLoader</c> is not
/// <see cref="IAsyncDisposable"/>), so without the registration the spawned
/// Chrome leaks on a long-running host (it OS-reaps only on host exit). Asserted
/// without launching a browser: the lazy launch never fires for a build-only
/// engine, so this runs in CI with no browser dependency.
/// </summary>
public sealed class CdpLoaderTeardownTests
{
    [Fact]
    public async Task WithCdpPageLoader_options_registers_the_transport_for_teardown()
    {
        var builder = ScraperEngineBuilder
            .Crawl("https://example.com")
            .AsMarkdown()
            .WithCdpPageLoader(new CdpLaunchOptions());

        // The registration happens when the transport factory runs (at build),
        // not at WithCdpPageLoader call time, so nothing is registered yet.
        Assert.Equal(0, builder.TeardownHookCountForTests);

        await using var engine = await builder.BuildAsync();

        // After BuildAsync the transport has registered itself, so disposing the
        // engine tears it down (and kills any spawned browser) instead of leaking.
        Assert.Equal(1, builder.TeardownHookCountForTests);
    }
}
