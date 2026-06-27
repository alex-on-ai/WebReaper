# WebReaper.AI

LLM extraction satellite for [WebReaper](https://github.com/alex-on-ai/WebReaper). Bring your own model: OpenAI, Anthropic, Ollama, Azure OpenAI, llamafile, anything implementing `IChatClient` (Microsoft.Extensions.AI).

## Install

```bash
dotnet add package WebReaper.AI
```

## What's in this package

Per [ADR-0044](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0044-llm-extractor-satellite.md), [ADR-0046](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0046-extraction-router.md), [ADR-0047](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0047-self-healing-selectors.md), [ADR-0050](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0050-semantic-page-actions.md), [ADR-0051](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0051-agent-crawl-driver.md), [ADR-0064](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0064-use-ai-policy.md), [ADR-0067](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0067-schema-inferrer-seam.md):

| Builder call | Purpose |
|---|---|
| `.WithLlmExtractor(chatClient)` | Pure-LLM `IContentExtractor`; bypasses the deterministic fold |
| `.WithLlmFallback(chatClient)` | Deterministic primary, LLM only when a field returns empty |
| `.WithLlmSelfHealing(chatClient)` | LLM proposes a repaired CSS selector once per (Schema, field), cached |
| `.WithLlmSchemaInferrer(chatClient)` | Synthesize a `Schema` from a URL with no hand-authored selectors |
| `.WithLlmAgentBrain(chatClient)` | LLM-powered `IAgentBrain` for the autonomous agent driver |
| `.WithLlmActionResolver(chatClient)` | Resolves `PageAction.SemanticAct("click 'sign in'")` to concrete arms |
| `.UseAi(chatClient)` | One-line aggregator wiring the recommended subset of the above |

## Quick start

```csharp
using Microsoft.Extensions.AI;
using OpenAI;
using WebReaper.AI;
using WebReaper.Builders;

IChatClient chatClient = new OpenAIClient("sk-...").AsChatClient("gpt-4o-mini");

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com")
    .Extract(schema)
    .WithLlmFallback(chatClient)
    .WriteToJsonFile("out.jsonl")
    .BuildAsync();

await engine.RunAsync();
```

The deterministic fold runs first. The LLM fires only when a field returns empty (a selector drifted, the page changed). Stable pages cost zero LLM calls.

## Design

`WebReaper.AI` is a satellite package per [ADR-0009](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0009-registration-seam-and-satellite-adapters.md): the LLM dependencies (`Microsoft.Extensions.AI.Abstractions`) stay quarantined off the dependency-light, Native-AOT-clean WebReaper core. The consumer's `IChatClient` makes the actual LLM call.

`LlmCall<T>` (the shared mechanism, [ADR-0059](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0059-llm-call-mechanism-module.md)) handles JSON-mode parsing with bounded retry, system-prompt caching ([ADR-0065](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0065-llm-call-system-prompt-caching.md)), and cost telemetry ([ADR-0066](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0066-engine-cost-telemetry.md)).

## See also

- Main repo: [github.com/alex-on-ai/WebReaper](https://github.com/alex-on-ai/WebReaper)
- All AI features showcased in [`Examples/WebReaper.AiNativeShowcase`](https://github.com/alex-on-ai/WebReaper/tree/master/Examples/WebReaper.AiNativeShowcase)
- Schema inference demos in [`Examples/WebReaper.SchemaInferenceShowcase`](https://github.com/alex-on-ai/WebReaper/tree/master/Examples/WebReaper.SchemaInferenceShowcase)
- License: [MIT](https://github.com/alex-on-ai/WebReaper/blob/master/LICENSE.txt) ([ADR-0017](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0017-relicense-gpl-mit.md))
