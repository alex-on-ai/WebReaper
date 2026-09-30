# WebReaper.AI.Http

An AOT-clean, OpenAI-compatible `IChatClient` (Microsoft.Extensions.AI) for WebReaper. Raw `HttpClient` plus System.Text.Json source generation, no provider SDK, so it composes into a Native-AOT binary such as the WebReaper CLI.

Point it at any OpenAI-compatible `/chat/completions` endpoint and hand it to WebReaper's `WithLlmExtractor` / `WithLlmSchemaInferrer`.

```csharp
using WebReaper.AI.Http;
using WebReaper.Builders;

// OpenAI
var client = new OpenAiCompatibleChatClient(
    baseUrl: "https://api.openai.com/v1",
    model: "gpt-4o-mini",
    apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

// or local Ollama (no key)
// var client = new OpenAiCompatibleChatClient("http://localhost:11434/v1", "llama3.2");

await ScraperEngineBuilder
    .Crawl("https://example.com")
    .AsMarkdown()
    .WithLlmExtractor(client)
    .BuildAsync();
```

## Scope

JSON-mode chat completions, which is all the schema-free `--prompt` and inferred-schema `--infer` extraction paths need (ADR-0084). Tool calling (the agent and action-resolver path) throws `NotSupportedException` rather than silently dropping the tools; use a tool-calling-capable `IChatClient` for those.

JSON mode sends OpenAI's `response_format: {"type": "json_object"}`. Some OpenAI-compatible servers reject that value (LM Studio accepts only `json_schema` and `text`). When a server answers 400 naming `response_format`, the client sends the request once more without it and, once that works, omits it on later calls; the prompt then carries the JSON instruction on its own.

## Why it exists

Microsoft.Extensions.AI's own OpenAI client is not AOT-tested. This is the AOT-safe bring-your-own chat client the .NET ecosystem otherwise lacks, shipped under the WebReaper umbrella so the CLI can offer one-command AI extraction without leaving the Native-AOT story.
