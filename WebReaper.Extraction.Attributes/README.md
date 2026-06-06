# WebReaper.Extraction.Attributes

`[ScrapeSchema]` and `[ScrapeField]` marker attributes for [WebReaper](https://github.com/alex-on-ai/WebReaper)'s Roslyn source generator. Consumed by [`WebReaper.Extraction.Generators`](https://www.nuget.org/packages/WebReaper.Extraction.Generators) at compile time. Lightweight; no runtime dependencies beyond the BCL.

## Install

You usually want both packages together:

```bash
dotnet add package WebReaper.Extraction.Attributes
dotnet add package WebReaper.Extraction.Generators
```

## What's in this package

Multi-targeted to `netstandard2.0` (so the Roslyn analyzer can reference it) and `net10.0` (so user code can use it at runtime). Ships three public types:

| Type | Purpose |
|---|---|
| `[ScrapeSchema]` | Class-level marker. The source generator emits a `static Schema Schema` and a `static Materialize(JsonObject)` on partial classes carrying this attribute. |
| `[ScrapeField(selector, ...)]` | Property-level marker. Maps a CSS selector to a property; supports `Type`, `IsList`, `Attr` parameters. |
| `SchemaFieldType` | Enum (`String`, `Integer`, `Float`, `Boolean`, `Datetime`) for typed field coercion. |

## Quick start

```csharp
using WebReaper.Extraction.Attributes;

[ScrapeSchema]
public partial class Article
{
    [ScrapeField("h1")]
    public string? Title { get; set; }

    [ScrapeField(".views", Type = SchemaFieldType.Integer)]
    public int Views { get; set; }

    [ScrapeField(".tag", IsList = true)]
    public List<string> Tags { get; set; } = new();

    [ScrapeField("a.permalink", Attr = "href")]
    public string? Permalink { get; set; }
}
```

The `WebReaper.Extraction.Generators` Roslyn analyzer emits, at compile time:

```csharp
public partial class Article
{
    public static Schema Schema { get; }
    public static Article Materialize(JsonObject json) { ... }
}
```

No reflection at runtime; AOT-clean. Schema typos become compile errors.

## See also

- Main repo: [github.com/alex-on-ai/WebReaper](https://github.com/alex-on-ai/WebReaper)
- The source generator: [`WebReaper.Extraction.Generators`](https://www.nuget.org/packages/WebReaper.Extraction.Generators)
- Design: [ADR-0045](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0045-scrape-schema-source-generator.md)
- License: [MIT](https://github.com/alex-on-ai/WebReaper/blob/master/LICENSE.txt)
