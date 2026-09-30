using System.Diagnostics;
using WebReaper.Cli.Stealth;
using WebReaper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace WebReaper.Cli.Tests;

/// <summary>One <see cref="LocalTestSite"/> per CLI E2E class — the subprocess
/// CLI hits it over loopback.</summary>
public sealed class CliSiteFixture : IAsyncLifetime
{
    public LocalTestSite Site { get; private set; } = null!;
    public async Task InitializeAsync() => Site = await LocalTestSite.StartAsync();
    public async Task DisposeAsync() => await Site.DisposeAsync();
}

/// <summary>
/// End-to-end coverage of the published CLI surface — the real binary, run as a
/// subprocess against the deterministic local site. The CLI dll is copied into
/// this test's output via the WebReaper.Cli ProjectReference, so it is invoked
/// as <c>dotnet WebReaper.Cli.dll …</c> (a normal managed run — PublishAot only
/// affects `dotnet publish`). Asserts stdout content and exit codes
/// (0 success, 2 usage error per Program.cs).
/// </summary>
[Trait("Category", "Cli")]
public sealed class CliEndToEndTests : IClassFixture<CliSiteFixture>
{
    private readonly LocalTestSite _site;
    private readonly ITestOutputHelper _output;

    public CliEndToEndTests(CliSiteFixture fixture, ITestOutputHelper output)
    {
        _site = fixture.Site;
        _output = output;
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    // Resolve the CLI from its OWN build output (self-consistent
    // runtimeconfig.json + deps.json). The copy that lands in this test's output
    // dir has no WebReaper.Cli.deps.json, so `dotnet` there can't resolve the
    // CLI's exact dependency set (e.g. Microsoft.Extensions.Logging.Abstractions
    // 10.0.0) on a clean runner — it only worked locally where that assembly was
    // already in a NuGet fallback. Same fix as the MCP server test.
    private static string ResolveCliDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebReaper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var sep = Path.DirectorySeparatorChar;
        var config = baseDir.Contains($"{sep}Release{sep}") ? "Release" : "Debug";
        var cliDll = Path.Combine(dir!.FullName, "WebReaper.Cli", "bin", config, "net10.0", "WebReaper.Cli.dll");

        Assert.True(File.Exists(cliDll), $"CLI dll not found at {cliDll}");
        return cliDll;
    }

    // A null env value removes that variable from the CLI's environment.
    private async Task<CliResult> RunCli(IEnumerable<string> args, params (string Name, string? Value)[] env)
    {
        var cliDll = ResolveCliDll();

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (name, value) in env)
        {
            if (value is null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        _output.WriteLine($"$ dotnet WebReaper.Cli.dll {string.Join(' ', args)}");
        _output.WriteLine($"exit={p.ExitCode}\n--stdout--\n{stdout}\n--stderr--\n{stderr}");
        return new CliResult(p.ExitCode, stdout, stderr);
    }

    [Fact]
    public async Task Scrape_outputs_markdown_to_stdout_by_default()
    {
        var r = await RunCli(["scrape", _site.Url("/static")]);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Widget Pro 3000", r.Stdout);
    }

    [Fact]
    public async Task Scrape_with_schema_emits_the_extracted_fields()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), $"wr-schema-{Guid.NewGuid():N}.json");
        // ADR-0043 schema shape: { children: [ { field, selector } ] }.
        await File.WriteAllTextAsync(schemaPath, """
        {
          "children": [
            { "field": "title", "selector": ".title" },
            { "field": "price", "selector": ".price" }
          ]
        }
        """);

        try
        {
            var r = await RunCli(["scrape", _site.Url("/static"), "--schema", schemaPath]);

            Assert.Equal(0, r.ExitCode);
            Assert.Contains("Widget Pro 3000", r.Stdout);
            Assert.Contains("$49.99", r.Stdout);
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public async Task Scrape_with_output_writes_a_file()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"wr-out-{Guid.NewGuid():N}.txt");

        try
        {
            var r = await RunCli(["scrape", _site.Url("/static"), "--output", outPath]);

            Assert.Equal(0, r.ExitCode);
            Assert.True(File.Exists(outPath));
            Assert.Contains("Widget Pro 3000", await File.ReadAllTextAsync(outPath));
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public async Task Crawl_sweeps_the_whole_site_and_streams_multiple_pages()
    {
        // ADR-0081: a whole-site sweep from the root. It follows on-domain links
        // (root → /static, /list, /item/1; pagination → page 2 → /item/4..6) and
        // is seeded by the sitemap (/item/1..3, /static); the off-domain
        // example.com links are dropped, so it does NOT hang on the network. The
        // Visited-link tracker dedups and the frontier saturates, so the
        // subprocess returns (a non-terminating sweep would hang the test).
        var r = await RunCli(["crawl", _site.BaseUrl, "--max-pages", "50"]);

        Assert.Equal(0, r.ExitCode);
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 5, $"expected several swept pages, got {lines.Length}");
        Assert.Contains("Widget Pro 3000", r.Stdout);   // /static, extracted as Markdown
        Assert.Contains("Item 1", r.Stdout);            // a leaf item page, swept + extracted
    }

    [Fact]
    public async Task Crawl_with_schema_emits_json_lines_for_swept_pages()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), $"wr-crawl-schema-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(schemaPath, """
        { "children": [ { "field": "title", "selector": ".title" } ] }
        """);

        try
        {
            var r = await RunCli(["crawl", _site.BaseUrl, "--schema", schemaPath, "--max-pages", "50"]);

            Assert.Equal(0, r.ExitCode);
            Assert.Contains("Widget Pro 3000", r.Stdout);   // /static .title
            Assert.Contains("\"url\"", r.Stdout);           // JSON records, url folded in
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public async Task Map_discovers_urls_from_sitemap_and_root_page()
    {
        var r = await RunCli(["map", _site.BaseUrl]);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("/static", r.Stdout);   // present in both sitemap and root links
    }

    [Fact]
    public async Task Version_prints_and_exits_zero()
    {
        var r = await RunCli(["version"]);

        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }

    [Fact]
    public async Task Unknown_command_exits_two()
    {
        var r = await RunCli(["frobnicate"]);

        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public async Task Scrape_without_url_is_a_usage_error()
    {
        var r = await RunCli(["scrape"]);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("Missing <url>", r.Stderr);
    }

    // ----- stealth install / path (#264), offline: an existing install is
    // found, so nothing is downloaded -----

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wr-cloak-{Guid.NewGuid():N}");
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public async Task Stealth_path_prints_the_binary_path_override()
    {
        var binary = CreateTempFile();
        try
        {
            var r = await RunCli(["stealth", "path", "cloakbrowser"], ("CLOAKBROWSER_BINARY_PATH", binary));

            Assert.Equal(0, r.ExitCode);
            Assert.Equal(binary, r.Stdout.Trim());
        }
        finally
        {
            File.Delete(binary);
        }
    }

    [Fact]
    public async Task Stealth_install_reuses_an_existing_binary_instead_of_downloading()
    {
        var binary = CreateTempFile();
        try
        {
            var r = await RunCli(["stealth", "install", "cloakbrowser", "--yes"], ("CLOAKBROWSER_BINARY_PATH", binary));

            Assert.Equal(0, r.ExitCode);
            Assert.Contains("already installed", r.Stdout);
            Assert.Contains(binary, r.Stdout);
        }
        finally
        {
            File.Delete(binary);
        }
    }

    [Fact]
    public async Task Stealth_path_rejects_a_binary_path_override_to_a_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"wr-missing-{Guid.NewGuid():N}");

        var r = await RunCli(["stealth", "path", "cloakbrowser"], ("CLOAKBROWSER_BINARY_PATH", missing));

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("CLOAKBROWSER_BINARY_PATH", r.Stderr);
    }

    [Fact]
    public async Task Stealth_path_finds_a_cloakbrowser_wrapper_install()
    {
        // The #264 layout: the npm/pip wrapper's cache (relocated here with
        // CLOAKBROWSER_CACHE_DIR) holding chromium-<build>/<executable>. A build
        // number no real install has keeps the CLI's own cache out of the lookup.
        var build = KnownStealthBackends.Find("cloakbrowser")!.BuildFor(StealthInstaller.CurrentPlatform());
        if (build is null) return; // upstream publishes no build for this machine
        const string version = "999.0.0.0.1";
        var cacheDir = Path.Combine(Path.GetTempPath(), $"wr-cloak-{Guid.NewGuid():N}");
        var exe = Path.Combine([cacheDir, $"chromium-{version}", .. build.Executable.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = await RunCli(
                ["stealth", "path", "cloakbrowser", "--version", version],
                ("CLOAKBROWSER_CACHE_DIR", cacheDir), ("CLOAKBROWSER_BINARY_PATH", null));

            Assert.Equal(0, r.ExitCode);
            Assert.Equal(exe, r.Stdout.Trim());
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stealth_list_shows_the_curated_backends()
    {
        var r = await RunCli(["stealth", "list"]);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("cloakbrowser", r.Stdout);
    }
}
