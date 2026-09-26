using WebReaper.Cli;
using Xunit;

namespace WebReaper.Cli.Tests;

// ADR-0084: the CLI AI-extraction flag logic (--prompt / --infer / --model /
// --llm-url), the mutual-exclusivity rule, the explicit-config client builder,
// and the per-page cost guard.
public class AiExtractionTests
{
    [Fact]
    public void Parse_prompt_captures_instruction_and_config()
    {
        var ai = AiExtraction.Parse(Args.Parse(
            ["scrape", "u", "--prompt", "find execs", "--model", "m", "--llm-url", "http://x/v1"]));

        Assert.True(ai.Any);
        Assert.Equal("find execs", ai.Prompt);
        Assert.False(ai.InferRequested);
        Assert.Equal("m", ai.Model);
        Assert.Equal("http://x/v1", ai.LlmUrl);
    }

    [Fact]
    public void Parse_valueless_prompt_throws()
        => Assert.Throws<CliException>(
            () => AiExtraction.Parse(Args.Parse(["scrape", "u", "--prompt"])));

    [Fact]
    public void Parse_infer_with_goal()
    {
        var ai = AiExtraction.Parse(Args.Parse(["crawl", "u", "--infer", "all execs"]));

        Assert.True(ai.InferRequested);
        Assert.Equal("all execs", ai.InferGoal);
        Assert.Null(ai.Prompt);
        Assert.True(ai.Any);
    }

    [Fact]
    public void Parse_valueless_infer_requests_with_null_goal()
    {
        var ai = AiExtraction.Parse(Args.Parse(["crawl", "u", "--infer"]));

        Assert.True(ai.InferRequested);
        Assert.Null(ai.InferGoal);
    }

    [Fact]
    public void Parse_no_ai_flags_is_not_any()
        => Assert.False(AiExtraction.Parse(Args.Parse(["scrape", "u"])).Any);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void ValidateExclusive_allows_at_most_one_strategy(bool prompt, bool infer, bool schema)
    {
        var ai = new AiExtractionContext(prompt ? "x" : null, infer, null, null, null);
        AiExtraction.ValidateExclusive(ai, schema ? "s.json" : null); // must not throw
    }

    [Fact]
    public void ValidateExclusive_rejects_prompt_plus_schema()
        => Assert.Throws<CliException>(() =>
            AiExtraction.ValidateExclusive(new AiExtractionContext("x", false, null, null, null), "s.json"));

    [Fact]
    public void ValidateExclusive_rejects_prompt_plus_infer()
        => Assert.Throws<CliException>(() =>
            AiExtraction.ValidateExclusive(new AiExtractionContext("x", true, null, null, null), null));

    [Fact]
    public void CreateClient_with_explicit_model_and_url_succeeds()
    {
        var ai = new AiExtractionContext("x", false, null, "gpt-4o-mini", "https://api.openai.com/v1");
        using var client = AiExtraction.CreateClient(ai);
        Assert.NotNull(client);
    }

    // The cost-guard cases pin interactivity and the reply source instead of
    // reading the test host's stdin, which is the terminal's when dotnet test
    // runs in one. TextReader.Null answers any prompt with EOF, which aborts, so
    // a case that expects no prompt fails instead of hanging if the guard asks.

    [Fact]
    public void CostGuard_never_blocks_automation()
    {
        // A non-TTY run (CI / pipe) must return without prompting for every
        // shape: automation is never blocked.
        var prompt = new AiExtractionContext("x", false, null, "m", "u");
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: false, interactive: false, input: TextReader.Null);
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: true, interactive: false, input: TextReader.Null);
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 10, yes: false, interactive: false, input: TextReader.Null);
        // --infer is never guarded (about one LLM call).
        AiExtraction.ConfirmCrawlCostOrThrow(
            new AiExtractionContext(null, true, null, "m", "u"),
            maxPages: 5000, yes: false, interactive: false, input: TextReader.Null);
    }

    [Fact]
    public void CostGuard_interactive_skips_the_prompt_for_yes_small_crawls_and_infer()
    {
        var prompt = new AiExtractionContext("x", false, null, "m", "u");
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: true, interactive: true, input: TextReader.Null);
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 10, yes: false, interactive: true, input: TextReader.Null);
        AiExtraction.ConfirmCrawlCostOrThrow(
            new AiExtractionContext(null, true, null, "m", "u"),
            maxPages: 5000, yes: false, interactive: true, input: TextReader.Null);
    }

    [Fact]
    public void CostGuard_interactive_large_prompt_crawl_asks_first()
    {
        // "y" proceeds; "n" or EOF aborts.
        var prompt = new AiExtractionContext("x", false, null, "m", "u");
        AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: false, interactive: true, input: new StringReader("y"));
        Assert.Throws<CliException>(() => AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: false, interactive: true, input: new StringReader("n")));
        Assert.Throws<CliException>(() => AiExtraction.ConfirmCrawlCostOrThrow(
            prompt, maxPages: 1000, yes: false, interactive: true, input: TextReader.Null));
    }
}
