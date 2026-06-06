using System.Globalization;
using System.Reflection;
using System.Text.Json;
using WebReaper.Cli.Commands;

namespace WebReaper.Cli;

// ADR-0082 Part 1: the update notifier. After a successful data command, an
// interactive user is told once (on stderr) that a newer release exists.
// Opt-out, TTY/CI/env-gated, 24h-throttled, read-only (a plain GitHub
// releases/latest GET, no telemetry payload). It never blocks the command,
// never fails it, and never touches stdout (the payload). AOT-clean: HttpClient
// + JsonDocument, no reflection beyond the assembly version attribute that
// VersionCommand already reads.
internal static class UpdateNotifier
{
    private const string DisableVar = "WEBREAPER_NO_UPDATE_CHECK";
    private const string LatestUrl =
        "https://api.github.com/repos/alex-on-ai/WebReaper/releases/latest";
    private const string InstallHint =
        "curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh -s -- --upgrade";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMilliseconds(1500);

    // ---- pure logic (unit-tested) ----

    // Opt-out gating: only an interactive (TTY stderr) run, not under CI, with
    // neither disable var set, checks for updates.
    internal static bool ShouldCheck(bool stderrIsTty, string? ciEnv, string? disableEnv, string? noNotifierEnv)
    {
        if (!stderrIsTty) return false;
        if (IsTruthy(ciEnv)) return false;
        if (!string.IsNullOrEmpty(disableEnv)) return false;
        if (!string.IsNullOrEmpty(noNotifierEnv)) return false;
        return true;
    }

    private static bool IsTruthy(string? v) =>
        !string.IsNullOrEmpty(v)
        && !v.Equals("0", StringComparison.Ordinal)
        && !v.Equals("false", StringComparison.OrdinalIgnoreCase);

    internal static bool IsStale(DateTimeOffset lastCheckUtc, DateTimeOffset nowUtc, TimeSpan ttl) =>
        nowUtc - lastCheckUtc > ttl;

    // True when latestTag is a strictly newer release than the running version.
    // Numeric (not lexical) compare; false when either side is unparseable (a
    // "dev" build never nags, garbage never triggers).
    internal static bool IsNewer(string latestTag, string currentVersion) =>
        TryParse(latestTag, out var l) && TryParse(currentVersion, out var c) && l.CompareTo(c) > 0;

    // Parse "vMAJOR.MINOR.PATCH(+build)(-pre)" to a comparable tuple: strip a
    // leading v and any +build metadata, then read the leading digit run of each
    // of the first three dotted components. False when major has no digits.
    private static bool TryParse(string raw, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var parts = s.Split('.');
        if (!TryLeadingInt(parts.ElementAtOrDefault(0), out var major)) return false;
        TryLeadingInt(parts.ElementAtOrDefault(1), out var minor);
        TryLeadingInt(parts.ElementAtOrDefault(2), out var patch);
        version = (major, minor, patch);
        return true;
    }

    private static bool TryLeadingInt(string? part, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(part)) return false;
        var i = 0;
        while (i < part.Length && char.IsAsciiDigit(part[i])) i++;
        return i > 0
            && int.TryParse(part.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // The channel-appropriate upgrade command, from where the binary lives.
    // Hardcoded prefix match (AOT-clean, no shelling to brew); separators
    // normalised so Windows paths match too.
    internal static string UpgradeHint(string? processPath)
    {
        var p = (processPath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/cellar/") || p.Contains("/opt/homebrew/") || p.Contains("linuxbrew"))
            return "brew upgrade webreaper";
        if (p.Contains("/winget/"))
            return "winget upgrade webreaper";
        if (p.Contains("/scoop/"))
            return "scoop update webreaper";
        return InstallHint;
    }

    internal static string FormatMessage(string latestTag, string currentVersion, string hint) =>
        $"\nwebreaper {Display(latestTag)} is available (you have {Display(currentVersion)}).\n" +
        $"Upgrade: {hint}\n" +
        $"(disable this check: {DisableVar}=1)";

    // Normalise a version string for display: drop a leading v and +build.
    private static string Display(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        return plus >= 0 ? s[..plus] : s;
    }

    // ---- orchestration (thin glue, best-effort) ----

    // Fire-and-forget after a successful data command. Any failure (offline,
    // rate-limited, malformed cache) is swallowed: the notifier must never
    // affect the command's exit code or output.
    internal static async Task MaybeNotifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ShouldCheck(
                    stderrIsTty: !Console.IsErrorRedirected,
                    ciEnv: Environment.GetEnvironmentVariable("CI"),
                    disableEnv: Environment.GetEnvironmentVariable(DisableVar),
                    noNotifierEnv: Environment.GetEnvironmentVariable("NO_UPDATE_NOTIFIER")))
                return;

            var cachePath = Path.Combine(BrowserCommand.GetWebReaperHome(), "update-check.json");
            var message = await EvaluateAsync(
                CurrentVersion(),
                Environment.ProcessPath,
                DateTimeOffset.UtcNow,
                () => ReadCache(cachePath),
                entry => WriteCache(cachePath, entry.LastCheck, entry.LatestTag),
                () => FetchLatestTagAsync(cancellationToken));

            if (message is not null) Console.Error.WriteLine(message);
        }
        catch
        {
            // Best-effort: a notifier failure is never the command's problem.
        }
    }

    // The orchestration decision, with I/O injected so it is unit-testable
    // without a network, a clock, or the filesystem. Returns the message to
    // print on stderr, or null when there is nothing to say. Honours the 24h
    // throttle: a fresh cache short-circuits the fetch; a stale or missing
    // cache fetches and re-stamps (keeping the prior tag if the fetch fails, so
    // an offline run does not retry every invocation).
    internal static async Task<string?> EvaluateAsync(
        string currentVersion,
        string? processPath,
        DateTimeOffset now,
        Func<(DateTimeOffset LastCheck, string? LatestTag)?> readCache,
        Action<(DateTimeOffset LastCheck, string? LatestTag)> writeCache,
        Func<Task<string?>> fetchLatest)
    {
        if (string.Equals(currentVersion, "dev", StringComparison.Ordinal)) return null;

        var cached = readCache();
        var latest = cached?.LatestTag;
        if (cached is null || IsStale(cached.Value.LastCheck, now, Ttl))
        {
            var fetched = await fetchLatest();
            latest = fetched ?? cached?.LatestTag;
            writeCache((now, latest));
        }

        return !string.IsNullOrEmpty(latest) && IsNewer(latest, currentVersion)
            ? FormatMessage(latest, currentVersion, UpgradeHint(processPath))
            : null;
    }

    private static string CurrentVersion() =>
        typeof(UpdateNotifier).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "dev";

    private static (DateTimeOffset LastCheck, string? LatestTag)? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("lastCheckUtc", out var lc)) return null;
            if (!DateTimeOffset.TryParse(lc.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var last))
                return null;
            var tag = root.TryGetProperty("latestTag", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            return (last, tag);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string path, DateTimeOffset now, string? latestTag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            using var w = new Utf8JsonWriter(fs);
            w.WriteStartObject();
            w.WriteString("lastCheckUtc", now.ToString("o", CultureInfo.InvariantCulture));
            if (latestTag is not null) w.WriteString("latestTag", latestTag);
            else w.WriteNull("latestTag");
            w.WriteEndObject();
        }
        catch
        {
            // Best-effort cache write.
        }
    }

    private static async Task<string?> FetchLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FetchTimeout);
            using var http = new HttpClient { Timeout = FetchTimeout };
            // GitHub's REST API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.Add("User-Agent", "webreaper-cli");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            using var resp = await http.GetAsync(LatestUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
