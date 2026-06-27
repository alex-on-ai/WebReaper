# WebReaper.Extraction.Generators

Roslyn source generator that emits a `static Schema` and a reflection-free `static Materialize` method on partial classes marked with `[ScrapeSchema]`. The .NET-native structural differentiator (REPOSITIONING-PLAN §2.3): Pydantic-parity that Python's runtime reflection structurally cannot match.

## Install

You usually want both packages together (this one is a compile-time analyzer; the attributes ship in a sibling package):

```bash
dotnet add package WebReaper.Extraction.Generators
dotnet add package WebReaper.Extraction.Attributes
```

`WebReaper.Extraction.Generators` is a `DevelopmentDependency=true` analyzer; it does not propagate to your project's runtime dependency graph.

## What's emitted

For each class marked with `[ScrapeSchema]`, the generator emits:

```csharp
public partial class Article
{
    public static Schema Schema { get; }
    public static Article Materialize(JsonObject json);
}
```

`Schema` is built once at compile time from the `[ScrapeField]` attributes on the class's properties. `Materialize` is reflection-free; the AOT publish trims and inlines it.

## Quick start

```csharp
using WebReaper.Extraction.Attributes;
using WebReaper.Builders;

[ScrapeSchema]
public partial class Article
{
    [ScrapeField("h1")]                                              public string? Title { get; set; }
    [ScrapeField(".views", Type = SchemaFieldType.Integer)]          public int Views { get; set; }
    [ScrapeField(".tag", IsList = true)]                             public List<string> Tags { get; set; } = new();
}

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/post")
    .Extract(Article.Schema)
    .Subscribe(p => HandleArticle(Article.Materialize(p.Data)))
    .BuildAsync();
```

## v1 scope

Common case only:
- Single-level schemas
- Primitive fields (`string`, `int`, `bool`, `DateTime`, `float`)
- `List<T>` of primitives

Nested `[ScrapeSchema]` types are explicitly deferred to a future version. The attributes package supports the syntax; the generator does not yet emit code for nested classes.

## See also

- Main repo: [github.com/alex-on-ai/WebReaper](https://github.com/alex-on-ai/WebReaper)
- The attributes: [`WebReaper.Extraction.Attributes`](https://www.nuget.org/packages/WebReaper.Extraction.Attributes)
- Design: [ADR-0045](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0045-scrape-schema-source-generator.md)
- License: [MIT](https://github.com/alex-on-ai/WebReaper/blob/master/LICENSE.txt)
