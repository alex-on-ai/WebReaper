namespace WebReaper.Stealth.CloakBrowser;

/// <summary>
/// Options for <see cref="CloakBrowserBuilderExtensions.WithCloakBrowser"/>.
/// </summary>
public sealed class CloakBrowserOptions
{
    /// <summary>Run the launched CloakBrowser headless. Default <c>true</c>.
    /// CloakBrowser's vendor docs recommend <c>false</c> (headed) for the
    /// most aggressive bot-detectors (DataDome on hardened sites). Composes
    /// with <see cref="ResidentialProxy">residential proxies via
    /// IProxyProvider</see>.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>Documentation tag — not used directly, but the vendor's
    /// hardened-site recipe is residential proxy + headed mode. Set up
    /// proxies via the existing <c>WithProxy(...)</c> registration.</summary>
    internal bool ResidentialProxy => false; // placeholder for ADR cross-ref

    /// <summary>Auto-install policy. Default
    /// <see cref="AutoInstallPolicy.PromptLogger"/>: detect first, download
    /// from upstream if missing, log a license-acknowledgment line.</summary>
    public AutoInstallPolicy AutoInstall { get; set; } = AutoInstallPolicy.PromptLogger;

    /// <summary>Pin a CloakBrowser build for the current platform instead of
    /// the platform's pinned build (see the README): the release tag
    /// (<c>"chromium-v146.0.7680.177.5"</c>) or the bare build
    /// (<c>"146.0.7680.177.5"</c>). Not every release carries every platform;
    /// the installer reads the release's <c>SHA256SUMS</c> before downloading
    /// and fails fast when this platform's asset is missing. When <c>null</c>,
    /// each platform installs its pinned build.</summary>
    public string? Version { get; set; }

    /// <summary>Optional pre-installed binary path. When set, the satellite
    /// skips installer detection and uses this path directly. Takes precedence
    /// over the vendor's <c>CLOAKBROWSER_BINARY_PATH</c> env var.</summary>
    public string? ExecutablePath { get; set; }
}

/// <summary>Auto-install policy for CloakBrowser binary acquisition
/// (ADR-0054 + ADR-0055).</summary>
public enum AutoInstallPolicy
{
    /// <summary>On first use, detect the binary; if absent, download from
    /// upstream and log a one-line license acknowledgment via the wired
    /// <c>ILogger</c>. Default. Suitable for library scenarios; CI
    /// scenarios should set <see cref="NoPromptYes"/>.</summary>
    PromptLogger,

    /// <summary>Unattended: download silently without logging the
    /// license-acknowledgment line (the consumer is expected to have
    /// surfaced it elsewhere — README, env var doc, …).</summary>
    NoPromptYes,

    /// <summary>Disabled: throw if the binary is not pre-installed. Use
    /// in CI / airgapped scenarios where downloads are forbidden.
    /// Pre-installed means <see cref="CloakBrowserOptions.ExecutablePath"/>,
    /// <c>CLOAKBROWSER_BINARY_PATH</c>, the WebReaper cache, or the
    /// cloakbrowser npm / pip wrapper's cache.</summary>
    Disabled,
}
