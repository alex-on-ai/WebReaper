using Microsoft.Extensions.AI;
using WebReaper.AI;
using WebReaper.AI.Http;
using WebReaper.Builders;

namespace WebReaper.Cli;

// ADR-0084: the CLI's AI-extraction surface, shared by `scrape` and `crawl`.
//   --prompt "<instruction>"  schema-free per-page LLM extraction
//   --infer  ["<goal>"]       infer a schema once, then deterministic-fold
// Config is explicit (ADR-0084 Q4, "make me choose"): --model + --llm-url (or
// WEBREAPER_LLM_* env); the API key is read only from the environment, never a
// flag. The chat client is the AOT-safe WebReaper.AI.Http OpenAI-compatible one.
internal sealed record AiExtractionContext(
    string? Prompt,
    bool InferRequested,
    string? InferGoal,
    string? Model,
    string? LlmUrl)
{
    /// <summary>True when an AI extraction strategy was requested.</summary>
    public bool Any => Prompt is not null || InferRequested;
}

internal static class AiExtraction
{
    public static AiExtractionContext Parse(ParsedArgs args)
    {
        string? prompt = null;
        if (args.HasFlag("prompt"))
        {
            prompt = args.GetFlag("prompt");
            // The bool fallback ("true") means the flag was given with no value.
            if (prompt is null or "true")
                throw new CliException(
                    "--prompt needs an instruction, e.g. --prompt \"all C-level execs with name and title\".");
        }

        var inferRequested = args.HasFlag("infer");
        // --infer may carry an optional goal; a valueless --infer (bool "true")
        // means infer with no goal hint.
        var inferGoal = inferRequested && args.GetFlag("infer") is { } g && g != "true" ? g : null;

        return new AiExtractionContext(
            prompt, inferRequested, inferGoal, args.GetFlag("model"), args.GetFlag("llm-url"));
    }

    /// <summary>--prompt, --infer, and --schema are mutually exclusive
    /// extraction strategies.</summary>
    public static void ValidateExclusive(AiExtractionContext ai, string? schemaPath)
    {
        var count = (ai.Prompt is not null ? 1 : 0)
                  + (ai.InferRequested ? 1 : 0)
                  + (schemaPath is not null ? 1 : 0);
        if (count > 1)
            throw new CliException(
                "Choose one of --prompt, --infer, or --schema (mutually exclusive extraction strategies).");
    }

    /// <summary>Build the OpenAI-compatible chat client from explicit config
    /// (ADR-0084 Q4). Throws actionably when the model or endpoint is missing.</summary>
    public static OpenAiCompatibleChatClient CreateClient(AiExtractionContext ai)
    {
        var model = ai.Model ?? Environment.GetEnvironmentVariable("WEBREAPER_LLM_MODEL");
        var url = ai.LlmUrl ?? Environment.GetEnvironmentVariable("WEBREAPER_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("WEBREAPER_LLM_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(model))
            throw new CliException(
                "AI extraction needs a model: pass --model <id> or set WEBREAPER_LLM_MODEL.");
        if (string.IsNullOrWhiteSpace(url))
            throw new CliException(
                "AI extraction needs an endpoint: pass --llm-url <url> "
                + "(e.g. https://api.openai.com/v1) or set WEBREAPER_LLM_BASE_URL.");

        return new OpenAiCompatibleChatClient(url, model, apiKey);
    }

    /// <summary>Wire the requested strategy onto the seed. Call only when
    /// <see cref="AiExtractionContext.Any"/> is true.</summary>
    public static ScraperEngineBuilder Apply(ICrawlSeed seed, AiExtractionContext ai, IChatClient client) =>
        ai.Prompt is not null
            ? seed.ExtractWithPrompt(client, ai.Prompt)
            : seed.ExtractInferred(ai.InferGoal).WithLlmSchemaInferrer(client);

    /// <summary>ADR-0084 Q5 cost guard: `crawl --prompt` makes one LLM call per
    /// page. When interactive and the page cap exceeds the threshold, confirm
    /// before running. `--yes` skips; a non-TTY (CI / pipe) never blocks; only
    /// --prompt scales per page (--infer is ~1 call, so it is not guarded).</summary>
    public static void ConfirmCrawlCostOrThrow(AiExtractionContext ai, int maxPages, bool yes) =>
        ConfirmCrawlCostOrThrow(ai, maxPages, yes, interactive: !Console.IsInputRedirected, Console.In);

    // The guard with its console dependencies injected: whether a user can
    // answer, and where the answer is read from. Tests pin both, because the
    // test host's stdin is the terminal's when dotnet test runs in one.
    internal static void ConfirmCrawlCostOrThrow(
        AiExtractionContext ai, int maxPages, bool yes, bool interactive, TextReader input)
    {
        const int threshold = 50;
        if (ai.Prompt is null || yes || maxPages <= threshold || !interactive)
            return;

        Console.Error.Write(
            $"?  crawl --prompt makes one AI call per page (up to {maxPages}). Continue? [y/N] ");
        var reply = input.ReadLine()?.Trim();
        if (reply is null || !reply.Equals("y", StringComparison.OrdinalIgnoreCase))
            throw new CliException(
                "Aborted. Re-run with --yes to skip this prompt, or lower --max-pages.");
    }
}
