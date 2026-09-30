using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebReaper.Builders;
using WebReaper.Cdp;

namespace WebReaper.Stealth.CloakBrowser;

/// <summary>
/// ADR-0054 stealth-backend convention: a satellite ships one extension
/// method on <see cref="ScraperEngineBuilder"/> wiring its fork's launcher
/// + installer to <c>WithCdpPageLoader(string)</c>. CloakBrowser is the
/// first concrete satellite.
/// </summary>
public static class CloakBrowserBuilderExtensions
{
    /// <summary>
    /// Use CloakBrowser as the Dynamic-page transport. On first call:
    /// reuses an existing CloakBrowser binary (<see cref="CloakBrowserOptions.ExecutablePath"/>,
    /// <c>CLOAKBROWSER_BINARY_PATH</c>, the WebReaper cache, then the
    /// cloakbrowser npm / pip wrapper's cache); downloads the platform's
    /// pinned build from upstream if absent (subject to
    /// <see cref="CloakBrowserOptions.AutoInstall"/>); launches it with the
    /// stealth-fork's recommended flags; wires the resulting CDP endpoint
    /// into <see cref="CdpPageLoaderBuilderExtensions.WithCdpPageLoader(ScraperEngineBuilder, string)"/>.
    /// </summary>
    /// <remarks>
    /// Per ADR-0054: by using this satellite you accept CloakBrowser's
    /// binary license — see
    /// https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md.
    /// On first install, the satellite logs a one-line acknowledgment via
    /// the registered <see cref="ILogger"/>. For unattended scenarios,
    /// set <see cref="CloakBrowserOptions.AutoInstall"/> to
    /// <see cref="AutoInstallPolicy.NoPromptYes"/>.
    /// </remarks>
    public static ScraperEngineBuilder WithCloakBrowser(
        this ScraperEngineBuilder builder,
        CloakBrowserOptions? options = null)
    {
        var opts = options ?? new CloakBrowserOptions();

        // Sync-over-async at the builder boundary, matching the rest of
        // the builder surface. Detection is cheap (a few file probes); the
        // download is the slow path and runs on first use, not every call.
        // ILogger is supplied by the builder pipeline at LoadAsync time,
        // not at registration time — install + launch run at build time
        // here, so we use NullLogger. The transport's per-page work picks
        // up the real ILogger via the 4-arg factory.
        ILogger logger = NullLogger.Instance;
        var binaryPath = CloakBrowserInstaller.EnsureInstalledAsync(opts, logger, CancellationToken.None)
            .GetAwaiter().GetResult();
        var endpoint = CloakBrowserLauncher.LaunchAsync(binaryPath, opts, logger, CancellationToken.None)
            .GetAwaiter().GetResult();

        // ADR-0058: register the launched-endpoint's IAsyncDisposable with
        // the builder; engine teardown kills the CloakBrowser subprocess
        // via the LIFO-ordered teardown chain (await using on the engine).
        // Pre-ADR-0058 (v10.0.0) this dropped the disposable and the
        // process OS-reaped only on host exit — the named CLAUDE.md gotcha.
        return builder
            .WithCdpPageLoader(endpoint.CdpUrl)
            .OnTeardown(endpoint);
    }
}
