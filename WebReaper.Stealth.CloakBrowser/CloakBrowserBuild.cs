using System.Runtime.InteropServices;

namespace WebReaper.Stealth.CloakBrowser;

/// <summary>
/// One platform's pinned CloakBrowser build. Upstream does not publish every
/// platform in every release (macOS and linux-arm64 trail linux-x64 and
/// windows-x64, and the newest releases are Pro-only), so each platform pins
/// its own free build, mirroring the per-platform map the vendor's own npm /
/// pip / NuGet wrappers ship. <see cref="CloakBrowserOptions.Version"/>
/// overrides the pin for the current platform.
/// </summary>
/// <param name="Platform">The RID the build runs on (<c>linux-x64</c>,
/// <c>osx-arm64</c>, <c>win-x64</c>, ...).</param>
/// <param name="Version">The upstream build installed by default.</param>
/// <param name="Asset">The release asset's file name (<c>.tar.gz</c> or
/// <c>.zip</c>).</param>
/// <param name="Executable">The executable's <c>/</c>-separated path inside the
/// unpacked archive.</param>
/// <param name="SizeMb">Approximate download size, for the license log
/// line.</param>
internal sealed record CloakBrowserBuild(
    string Platform,
    string Version,
    string Asset,
    string Executable,
    int SizeMb)
{
    /// <summary>Every platform upstream publishes a free build for. A platform
    /// with no row (win-arm64, 32-bit) has none.</summary>
    internal static readonly CloakBrowserBuild[] All =
    [
        new("linux-x64", "146.0.7680.177.5", "cloakbrowser-linux-x64.tar.gz", "chrome", SizeMb: 217),
        new("linux-arm64", "146.0.7680.177.3", "cloakbrowser-linux-arm64.tar.gz", "chrome", SizeMb: 208),
        new("osx-arm64", "145.0.7632.109.2", "cloakbrowser-darwin-arm64.tar.gz", "Chromium.app/Contents/MacOS/Chromium", SizeMb: 147),
        new("osx-x64", "145.0.7632.109.2", "cloakbrowser-darwin-x64.tar.gz", "Chromium.app/Contents/MacOS/Chromium", SizeMb: 159),
        new("win-x64", "146.0.7680.177.5", "cloakbrowser-windows-x64.zip", "chrome.exe", SizeMb: 562),
    ];

    /// <summary>The pinned build for <paramref name="platform"/> (a RID from
    /// <see cref="CurrentPlatform"/>), or <c>null</c> when upstream publishes
    /// none.</summary>
    internal static CloakBrowserBuild? For(string? platform) =>
        All.FirstOrDefault(b => string.Equals(b.Platform, platform, StringComparison.Ordinal));

    /// <summary>The current machine as a RID (<c>osx-arm64</c>,
    /// <c>linux-x64</c>, ...), or <c>null</c> on an OS or architecture with no
    /// name here. The OS architecture, not the process's: the browser runs as
    /// its own native process.</summary>
    internal static string? CurrentPlatform() => PlatformKey(
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsLinux() ? "linux"
        : null,
        RuntimeInformation.OSArchitecture);

    internal static string? PlatformKey(string? os, Architecture arch)
    {
        var cpu = arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null,
        };
        return os is null || cpu is null ? null : $"{os}-{cpu}";
    }
}
