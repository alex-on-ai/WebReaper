# WebReaper

The domain language of WebReaper — a declarative, parallel web crawler. This file names the concepts a contributor must share to reason about the crawl correctly; it is the reference the architecture review defers to.

## Language

### Crawl topology

**Crawl**:
One end-to-end run of the scraper over a site, seeded from the configured start URLs.
_Avoid_: scrape, scan, session.

**Crawl seed**:
The start URL(s) and page mode you hold *before* a builder exists, produced by the static `ScraperEngineBuilder.Crawl(...)` / `CrawlWithBrowser(...)`, awaiting an extraction strategy. Its operations are the strategy terminals: `Extract(schema)` (deterministic fold), `AsMarkdown()` (no-schema Markdown, ADR-0040), `ExtractInferred(goal?)` (inferred schema, ADR-0067), and `ExtractWithPrompt(instruction)` (schema-free **Prompt extraction**, ADR-0084). Each yields the configurable builder; the build terminals live only there, so "build with no start URLs or no extraction strategy" is unrepresentable rather than a runtime throw (see `docs/adr/0025-staged-builder-entry.md`).
_Avoid_: builder, config, prerequisites, start set.

**Job**:
A single URL to visit, plus the remaining selector chain and its parent backlinks; the unit the scheduler queues and a worker consumes.
_Avoid_: task, work item, request.

**Selector chain**:
The ordered queue of `LinkPathSelector`s a Job has left to apply before it reaches a target page; its length and its head's pagination flag *are* the crawl state. The grammar is enforced at the construction site (ADR-0030): the `LinkPathSelector` primary constructor rejects an empty `Selector`, an empty (non-null) `PaginationSelector`, and `PageActions` carrying actions when `PageType` is `Static` (the HTTP transport ignores them — a silent feature-drop). `LinkPathSelector.Follow(selector, …)`, `LinkPathSelector.Paginate(itemSelector, paginationSelector, …)`, and `LinkPathSelector.Sweep(selector, …)` (the recursive on-domain **Site sweep** shape, ADR-0081) are the named factories for the three intent-shapes, siblings to `Schema.ListOf`; `Paginate` additionally requires the pagination selector (a `null` one is the valid plain-follow shape the constructor must round-trip, so that intent-level rule lives on the factory).
_Avoid_: selector list, path, route.

**Page category**:
Which of four roles a Job's page plays — **derived** from the selector chain, never stored on the Job. It is realized as the arm of the **Crawl outcome** the **Crawl step** returns, not as a property of the input (the old `Job.PageCategory` was removed).
_Avoid_: page type (that is the Static/Dynamic load mode — see below), state, kind.

- **Target page**: a Job whose selector chain is empty; its page is parsed with the Schema, yields one ParsedData, and produces no further Jobs.
- **Transit page**: a Job with selectors remaining and no pagination on the head selector; its page yields child Jobs by following the head selector.
- **Page with pagination**: a Job with exactly one selector left that carries a pagination selector; its page yields both item Jobs and next-page Jobs.
- **Sweep page**: a Job under a recursive **Sweep** selector (ADR-0081); its page is parsed (yields one ParsedData) *and* its on-domain links are followed, the child Jobs **retaining** the sweep selector so the traversal perpetuates until the **Visited-link tracker** frontier saturates or the page-cap cutoff trips. The fourth role, the one page that both extracts and follows.

**Site sweep**:
A **Crawl** whose link-following is a recursive on-domain sweep rather than a fixed-length selector chain: from the start URL (optionally seeded by the **Site mapper**'s sitemap URLs), follow every on-domain `<a href>` breadth-first, parsing each page, until the **Visited-link tracker** frontier saturates or the page-cap cutoff trips (ADR-0081). It is realized as the fourth **Page category** (**Sweep page**) and its **Crawl outcome** arm (`Swept`); the CLI verb is `crawl`, the library method `.Sweep(...)`. On-domain is same-host-plus-`www` by default; `--include-subdomains` broadens it. Distinct from the **Site mapper**, which only discovers URLs in one pass and never extracts.
_Avoid_: full crawl, deep crawl, spider (the **Spider** is the per-Job I/O shell), recursive scrape.

### The crawl step

**Crawl step**:
The pure decision that maps a Job, its loaded document, and the config to exactly one crawl outcome. The Spider is the I/O shell around the crawl step, not the crawl step itself.
_Avoid_: spider, handler, processor.

**Crawl outcome**:
The result of a crawl step: a closed sum with four arms: a target page's ParsedData, a transit page's followed Jobs, a paginated page's item + next-page Jobs, or a **Sweep page**'s ParsedData together with its on-domain child Jobs (the `Swept` arm, ADR-0081). Never more than one arm; closed, with a new arm added only when a genuinely new **Page category** appears (ADR-0001 anticipated exactly this; `Swept` is its first exercise). See `docs/adr/0001-crawl-outcome-closed-sum.md`.
_Avoid_: result, response.

**Advance**:
What a transit page does to the selector chain — consume (dequeue) the head selector, so child Jobs carry the shortened chain.
_Avoid_: descend, pop, next.

**Retain**:
What a page with pagination does — leave the selector chain unchanged, so next-page Jobs re-apply the same step (page 2 of a listing is the same step, not a deeper one).
_Avoid_: repeat, stay, hold.

### The Crawl driver

**Crawl driver**:
The role that drives a Crawl: seed a Job per start URL, schedule Jobs, own the Crawl-global state (the Visited-link tracker and the stop rule), interpret each Job report, and detect termination. Two adapters — the in-process driver (`ScraperEngine`, `Parallel.ForEachAsync`) and the distributed driver (one queue message per Job). It holds one Spider and is the only thing that fans out to Sinks and fires the PostProcessor / ScrapedData notification (those moved off the Spider — see `docs/adr/0022-crawl-driver-and-outstanding-work-latch.md`).
_Avoid_: engine, runner, orchestrator, coordinator, master.

**Spider**:
The per-Job I/O shell: load one Job's page, run the Crawl step, return a Job report — nothing else. It does NOT own the Visited-link tracker, the crawl-limit stop, Sink fan-out, or the callbacks; those are the Crawl driver's. Its two run-scoped inputs — the headless flag and the parsing Schema — are supplied at construction, not re-read from config storage per Job; the shell holds no config storage (ADR-0034). The ADR 0001 posture one layer up: the shell *reports* what happened, the driver *decides* what to do.
_Avoid_: crawler, worker, handler, the spider that fans out.

**Distributed spider builder**:
The ADR-0009 reduced shell — `DistributedSpiderBuilder`, the seedless public seam a distributed worker uses to build its Spider. Config-agnostic by construction: no Crawl seed, no `BuildAsync`, no crawl-shape — the worker's `ScraperConfig` is authored and persisted by the start endpoint; the worker fetches it from shared storage and passes it to `BuildSpider(config)` (ADR-0034 — a required parameter, so a worker spider cannot be built without one). A separate type from the engine builder on purpose ("two seams, not one bug"); shared adapters are wired by hand via public satellite concretes / DI (see `docs/adr/0025-staged-builder-entry.md`).
_Avoid_: spider builder (unqualified), worker, the engine builder, ScraperEngineBuilder.

**Job report**:
The closed value `Spider.CrawlAsync` returns: the optional ParsedData to emit (present iff a Target page), the discovered child Jobs (unfiltered — dedup is the driver's), and the accounting facts the Outstanding-work latch needs (this Job's identity, its child count). It *wraps* the Crawl step's Crawl outcome with emission and accounting facts; it does not replace or extend the closed `CrawlOutcome` sum (ADR 0001 stands). Termination is a field here, never a thrown exception.
_Avoid_: result, outcome (that is the Crawl step's), response, crawl result.

**Visited-link tracker**:
Crawl-global state — what *the Crawl* has visited — owned by the Crawl driver, not the Spider. Its atomic test-and-set ("was this URL newly added?") is the single idempotency authority that gates three things at once: discovery dedup, the page-limit gate, and Outstanding-work-latch accounting. One atomic membership check, three uses; this is what makes the latch correct independent of at-least-once queue delivery.
_Avoid_: seen set, cache, dedup filter, link tracker (unqualified).

**Outstanding-work latch**:
The Crawl driver's completion detector: a centralized unit-credit counter (seed = #start URLs; `SignalProcessedAsync(childCount)` credits a Job's discovered children and returns the Job's own unit in one atomic step — net `childCount - 1`; reaching zero trips the latch exactly once, firing the idempotent end-of-crawl action via a compare-and-set). Credit conservation is structural (ADR-0032): crediting the children and returning the parent's unit are one operation, so the counter can never hit zero prematurely — the old two-call `AddAsync`-then-`SignalProcessedAsync` ordering is gone. One seam, two adapters: in-memory `Interlocked` (in-process driver) and a distributed-atomic counter (Redis/Cosmos, reusing ADR 0003/0005 infra). The latch detects **completion** only — the soft page limit is a distinct **cutoff**; composing the two into the stop verdict is the **Stop rule**'s job, not the latch's. See `docs/adr/0022-crawl-driver-and-outstanding-work-latch.md` and `docs/adr/0032-stop-rule-module.md`.
_Avoid_: pending counter, drain flag, semaphore, barrier.

**Stop rule**:
The in-process Crawl driver's one home for *"should this Crawl stop, and why?"* (`StopRule`, ADR-0032) — it composes the two termination conditions the driver previously checked inline: **completion** (the **Outstanding-work latch** drained) and **cutoff** (the soft page limit reached). The driver consults it — seed once, the `IsCrawlOver` pre-crawl gate, `RegisterProcessedAsync` per Job — instead of inlining latch calls and limit arithmetic; it *reports* the verdict and the driver *acts* — it ends its own consumption of the job stream (ADR-0037). Completion and cutoff stay distinct mechanisms — the latch is exact and consensus-requiring, a cutoff is soft and threshold-based; they are composed, never merged. The page limit is the only cutoff; a cutoff *family* (time budget, error-rate cap) is housed-for but deliberately not built. In-process only: the distributed driver is consumer-authored (ADR-0009) and consults the latch primitive directly.
_Avoid_: stop condition, terminator, limiter, the latch (that is one input).

**Retry policy**:
The wrapper around each per-Job Spider call (ADR-0026) that bounds retries on transient failures before the Crawl driver gives up on the Job. One seam (`IRetryPolicy.ExecuteAsync<T>`), one core default (`FixedAttemptsRetryPolicy` — four attempts total, no delay, every exception except `OperationCanceledException` triggers a retry). Custom policies (a Polly resilience pipeline with exponential backoff, a no-retry adapter for deterministic tests, a future satellite-aware policy) wire in through the **Registration seam** via `WithRetryPolicy`. Cancellation is cooperative by contract: an implementation that retries `OperationCanceledException` violates the seam. Lives only on the in-process Crawl driver; the distributed-worker reduced shell does not use one (its retry knob is the queue's redelivery / visibility-timeout).
_Avoid_: resilience policy, retry strategy, retrier.

### Extraction & output

**Schema**:
The declarative field→selector tree describing what to extract from a target page. The grammar is enforced at the construction site (ADR-0028): `Schema.Add` rejects children with empty `Field`, leaves with empty `Selector`, and list containers (`IsList = true`) with empty `Selector` — fast-failing at the add call. `Schema.ListOf(field, selector, …children)` is the named factory for list-of-objects, bundling the `IsList + Selector + Children` triple in one call. A non-list nested Schema is the one shape exempt from the selector rule — it uses the parent scope.
_Avoid_: template, mapping, model.

**Content extractor**:
The seam that turns one loaded target page into the structured record the **Sink**s emit (`IContentExtractor`, ADR-0039) — the content half of crawling a page (link discovery is the other half, the concrete `LinkExtractor`, ADR-0036). Named for the task, not its one core adapter: the axis of variation is extraction *strategy* (the deterministic **Schema fold**, the no-schema **Markdown extraction**, the schema-driven **LLM extractor**, and the schema-free **Prompt extraction** are the strategies), and that axis is independent of the **Node backend** (the document-shape axis, one level down). Selected via `WithContentExtractor`.
_Avoid_: content parser, JSON parser, the parser.

**Schema fold**:
The single recursive interpretation of a Schema against a loaded document — container vs object-list vs leaf vs value-list, type coercion, the missing-node policy, the swallow-and-log scope. It has exactly one home — `SchemaFold<TNode>`, the deterministic adapter of the **Content extractor** seam; a backend never re-implements it.
_Avoid_: walk, traversal, visitor, parser (the parser is the shell around the fold).

**Node backend**:
The per-document-shape seam the fold calls (`ISchemaBackend<TNode>`): parse a root, select-many, select-one, extract a leaf's **raw value**. HTML/CSS, JSON/JSONPath, and HTML/XPath are three backends; the seam is the only place document-specific quirks live.
_Avoid_: parser, loader, driver.

**Raw value**:
What a backend's `ExtractRaw` returns for a leaf *before* coercion — a `string` for text/markup backends, a native token for structured ones. An *untyped* leaf is this value verbatim; that single fact (not duplicated code) is the entire HTML-vs-JSON output difference.
_Avoid_: text, content, the value.

**Typed coercion**:
The Schema→CLR conversion the fold applies when `SchemaElement.Type` is set (`Integer`→`int.Parse`, …). Shared grammar, backend-independent — never a per-backend concern. Coercion-failure handling (ADR-0029): a per-leaf `FormatException` or `OverflowException` from `Coerce` is caught by the fold, the field is left unset, and the error is logged with a coercion-specific message that names the target type and the field; an unexpected exception (backend bug, malformed selector) is also caught with a distinct *unexpected error extracting field* message. A noisy page never aborts the crawl by design — the swallow-and-log policy is the documented contract, pinned by `TypedCoercionFailureTests`.
_Avoid_: parsing, casting, conversion (unqualified).

**ParsedData**:
The extracted record from one target page — its URL plus a JSON object. Its construction folds the URL into the JSON object under the key `"url"` (ADR-0031), so `Data` is the canonical emitted record; every **Sink** writes it as-is and none re-merges the URL. `"url"` is therefore a reserved key — a Schema field named `url` is overwritten by the page URL. The **Crawl driver** hands each **Sink** its own deep-clone of `Data` at fan-out, so concurrent **Sink**s never share a `JsonObject`.
_Avoid_: result, item, row.

**Page processor**:
A stage of the ordered pipeline the **Crawl driver** runs over each **Target page**'s **ParsedData** *before* the **Sink** fan-out (`IPageProcessor`, ADR-0038) — the one home for "react to an extracted page": enrich it, observe it, filter it, or replace/repair it. Registration order is pipeline order; processor N sees processor N−1's record. Sibling seam to the **Sink** — Process (in-pipeline, ordered, sees the raw page; the AI-hook surface) vs Emit (terminal, concurrent fan-out). Replaces the removed `PostProcess` callback; a processor that throws drops the page (the ADR-0029 posture). A processor needing one-time async work reuses **Adapter warm-up**.
_Avoid_: post-processor, callback, middleware, handler.

**Page verdict**:
What a **Page processor** returns — a closed two-arm sum (`PageVerdict`, the ADR-0001 lineage): `Kept` carries a record to the next stage and ultimately the **Sink**s (enrich / observe / repair are all `Kept`); `Dropped` filters the page so no **Sink** emits it.
_Avoid_: result, decision, outcome (that is the **Crawl outcome**).

**Sink**:
A destination a ParsedData is emitted to (file, Redis, Cosmos, …). The terminal half of the post-extraction surface — Emit, a concurrent fan-out, sibling to the in-pipeline **Page processor** (ADR-0038). `Subscribe` is sugar that registers a delegate **Sink**, not a separate notification seam.
_Avoid_: output, writer, exporter, subscription.

**Post-extraction pipeline**:
The one runtime module that owns the whole post-extraction surface for a single **ParsedData**: run it through the **Page processor** pipeline (Process), and if the **Page verdict** is `Kept`, fan it out to every **Sink** (Emit), returning the surviving record (or `null` when a processor dropped it). One fused `ProcessAndEmitAsync` so the drop-means-no-emit invariant lives in one place; the **Crawl driver** ignores the return, the **Agent driver** uses it for its run-scoped record bookkeeping (`AgentDecisionOutcome.Extracted`). Holds the processors and sinks and owns their lifecycle: it is itself an **Adapter warm-up** (`IAsyncInitializable`) and **Engine-teardown disposal chain** (`IAsyncDisposable`) participant, so each driver warms and disposes it as one adapter slotted between the **Visited-link tracker** and (on dispose) ahead of the tracker, never iterating the processor/sink collections itself. Public, so the consumer-authored distributed **Crawl driver** is a first-class third caller, not a re-implementer. Process and Emit stay distinct seams *inside* it (the **Page processor** and **Sink** interfaces are unchanged); the module composes them, it does not merge them. Distinct from the builder-side `ContentExtractorPipeline` (ADR-0072), which assembles the *extractor* stack at wiring time; this is the *runtime* path a record takes after extraction.
_Avoid_: emission pipeline (undersells the Process half), record handler, sink dispatcher, output pipeline, page-processor pipeline (that is only the Process half).

**File sink drain**:
The one home for the buffered file-write mechanism (`BufferedFileSink`): a
producer enqueues rows, one background consumer appends them to the file;
cleanup and directory creation happen once, eagerly, at construction. Every
file **Sink** is this drain plus a **Row format**; the drain never knows JSON
from CSV.
_Avoid_: writer, buffer, file sink (unqualified).

**Row format**:
The single quarantined per-format quirk a **File sink drain** delegates to
(`IFileSinkFormat`): how one row becomes a line, and whether the file carries a
header derived from the first row. JSON-lines and CSV are two **Row format**s —
the only legitimate difference between file sinks.
_Avoid_: serializer, formatter, encoder.

### Crawl-state persistence

**Keyed blob store**:
A backend-agnostic seam that persists and fetches one opaque UTF-8 string under a key, last-write-wins, with `null` meaning absent; its adapters are the only place the *keyed* persistence mechanism lives. The file adapter keeps its own whole-file replace/read but delegates directory creation, cleanup-on-start, and the missing-file policy to **File persistence prep**.
_Avoid_: repository, cache, document DB, config storage.

**Payload shell**:
The thin module above a **keyed blob store** that owns one payload's serialization grammar and quirks (the config shell owns its `System.Text.Json` source-gen grammar — ADR-0008, replacing `TypeNameHandling.Auto`; the cookie shell owns `CookieContainer ↔ CookieCollection`) and decides what *absent* means for that payload.
_Avoid_: storage, provider, serializer.

**Closed-sum codec**:
The one hand-written-but-shared JSON mechanism the flat **closed sum**s (`PageAction`, `AgentDecision`, `AgentDecisionOutcome`) describe their arms to, getting streaming `Read(ref Utf8JsonReader)` + `Write(Utf8JsonWriter, T)` for free (`ClosedSumCodec<T>` + a per-arm descriptor). The serialization-layer sibling of the **LLM call** mechanism: the mechanism owns the object envelope, the `type` discriminator write/dispatch, the one-time `JsonNode.Parse(ref reader)` materialization (the `ref struct`-reader can't be handed to a delegate, so the read side materializes once then dispatches per arm on a plain `JsonObject`), the single missing-field `Require` contract, the unknown-tag throw, and the common-field pass; each arm's descriptor owns only its tag, its field writes, and its build-from-`JsonObject`. Adding an arm is one descriptor row, not three edits across a write switch + read loop + read switch. AOT-clean (System.Text.Json.Nodes, no reflection — ADR-0008 holds); the materialize-then-dispatch allocation is irrelevant at these call frequencies (config load, agent resume, per-**Agent step**). Nesting is composition: a parent arm builds a child via the child codec's `From(JsonNode)` entry. Three codecs stay bespoke because they are not flat tag-sums — **Schema** (a recursive container/leaf tree on `$kind`, which the mechanism *composes with* via `From` but is not built by), `AgentRunSnapshot` (a product type with arrays), and the selector-chain / backlink `ImmutableQueue` converters; each keeps calling the migrated codecs' streaming `Read(ref r)` unchanged through a shim.
_Avoid_: closed-sum converter (the `JsonConverter<T>` wrapper is a thin shell over this), discriminated-union serializer, polymorphic codec, the codec (unqualified).

**File persistence prep**:
The one home for the three things every file-backed adapter must get right
*before* it writes: create the file's directory eagerly and unconditionally
(`Directory.CreateDirectory`, idempotent), apply `DataCleanupOnStart`
deterministically at start (not lazily on first write), and read a missing
file as empty rather than throwing. A small *stateless* helper — **not** a
held-handle durability layer. Durable append stays the adapter's own
per-call write or, where one already exists, a single-writer drain
(`BufferedFileSink`): concurrent durable append is a single-writer concern
(the **File sink drain**'s one-consumer pattern), deliberately not a shared
lock. A file **Keyed blob store** (whole-file replace), a file **Scheduler**
(its append + resumable cursor + position file), a file **Visited-link
tracker** (its append + in-memory mirror), and a **File sink drain** (its
buffered drain) each keep their own write/read essence and only delegate
this prep. The held-handle/lock *substrate* was considered and rejected as
over-built and un-idiomatic; `FileScheduler`'s file-as-queue → embedded
store is **decided** — adopted as the `WebReaper.Sqlite` satellite
(ADR-0012), *not* a core replacement: the core `FileScheduler` stays the
zero-dep default and SQLite is an opt-in robust-local tier (see
`docs/adr/0012-sqlite-embedded-store-satellite.md`).
_Avoid_: durable file / file substrate (the held-handle substrate was rejected — see Flagged ambiguities), file store, journal.

### Page loading

**Page loader**:
The single seam (`IPageLoader`) that turns a **PageRequest** into a **page load result** (body plus response status and headers, ADR-0083), dispatching on the request's **PageType** to one **load transport**; the Spider holds one of these and is loader-blind (no Static/Dynamic loader pair).
_Avoid_: page fetcher, downloader, static/dynamic loader.

**Load transport**:
The per-mechanism adapter behind the **page loader** — HTTP (`HttpPageLoadTransport`, in core), Playwright (browser SDK, `WebReaper.Playwright` satellite), or CDP-direct (raw protocol, `WebReaper.Cdp` satellite — the bedrock for **Browser backend** swaps) — and the only place that mechanism's client/launch quirks and its proxy application live. Browser transports launch and drive a **Browser backend** (the Chromium binary, a separate axis). ADR-0050's `(cookies, proxy, logger, actionResolver)` factory contract applies uniformly across transports. Returns a **page load result** for any completed response; only a no-response failure is a **page load exception** (ADR-0083).
_Avoid_: requester, loader, driver, channel.

**Browser backend**:
The Chromium binary a browser **Load transport** launches and drives — system Chrome (detected on `PATH`), managed Chromium (the CLI's `~/.webreaper/browsers/` cache, downloaded on first use), or a stealth Chromium fork (CloakBrowser, Patchright, …). One transport (the CDP-direct transport) drives many backends; each stealth fork has its own per-launch flag set and binary-discovery rules. The `WebReaper.Stealth.X` satellites are per-backend, not per-transport — they ship installer + launcher helpers that conform to ADR-0055's backend-fork detection registry. Distinct from **Load transport**: transport is *how* WebReaper drives the page-loader; backend is *which binary* it launches.
_Avoid_: transport (that is how we drive, not what we drive), driver, runtime.

**PageRequest**:
What the **page loader** needs to fetch one page — URL, **PageType**, optional page actions, headless flag — projected from a **Job** plus the crawl's headless setting (the loader never sees the selector chain or backlinks).
_Avoid_: load args, request.

**Page load result**:
The value a **page loader** / **load transport** returns for one page (ADR-0083): the page `Html` plus the response `HttpStatus` (nullable; null when a transport cannot determine it) and `Headers` (case-insensitive). Replaces the bare HTML string the loader returned before, so a consumer, and in later slices the **block detector**, can read the response metadata; it is the first time the library can report an HTTP status. An init-only record (deliberately not positional, so future fields like `FinalUrl` land additively). A completed response of any status is returned as one of these.
_Avoid_: response, page, document (that was the old string return).

**Page load exception**:
What a **load transport** throws when there is no HTTP response at all (ADR-0083): DNS failure, connection refused, TLS error, or timeout. A completed response with any status code, including 4xx and 5xx, is returned as a **page load result**, not thrown; a non-2xx is data, not a fault. This narrows ADR-0004's "a page that cannot be retrieved is an exception" stance to the genuine no-response case, and means the **retry policy** retries only these transport faults, never an HTTP status.
_Avoid_: load error, HTTP error, fetch failure.

### Block detection

**Block detector**:
The core `IBlockDetector` seam plus its default `BlockDetector`; a pure classifier over a **page load result** (status, headers, body) that answers "am I being blocked?". It reports, it does not act (ADR-0083); the verdict is data the **Crawl driver** reads, never a thrown signal. Ships in core (blocking is a core scraping concern), runs on every loaded page inside the Spider; swap it via `WithBlockDetector`.
_Avoid_: bot-check detector (that is the ADR-0056 CLI ancestor), challenge classifier, anti-bot guard.

**Block verdict**:
The `BlockVerdict` a **block detector** returns: a `BlockConfidence` tier (None, Weak, or High) plus a reason string. High = a challenge-class HTTP status (403 / 429 / 503) or a challenge-signalling response header; Weak = a challenge-structural body marker. Record count is not an input (it re-enters the decision at the driver in a later slice, which is what keeps a weak body-marker false positive from destroying a real page).
_Avoid_: block result, detection result, score.

**Blocked page**:
A **page load result** the **block detector** classifies as a challenge (`IsBlocked`, i.e. confidence above None). Load-stage and reliable: it is read straight off the response, before extraction. It is the signal that drives the **escalating page loader** to climb to a stronger transport tier; if the page is still blocked at the top tier the **Crawl driver** suppresses it per the **block drop policy** and tallies the drop into the **run report**'s `BlockedPageCount`. Distinct from an **Empty result**, which is a weaker extraction-stage hint.
_Avoid_: blocked response, challenge page, captcha page.

**Block drop policy**:
The pure `BlockDropPolicy` rule (ADR-0083 part 8) the **Crawl driver** consults to decide whether to suppress a **blocked page** rather than emit it: a **block verdict** plus the page's extracted record count in, a keep-or-drop decision out. High confidence drops always; Weak drops only when the page also yielded zero records (a weak body-marker page that still extracted real records was a false positive and is kept); None never drops. A dropped page skips the **Page processor** pipeline and the **Sink** fan-out entirely, so challenge content never reaches a consumer's store. This is the one place record count re-enters the design: an extraction-stage fact the driver folds in one layer above the **block detector**, never back inside the pure load-stage seam.
_Avoid_: suppression filter, block filter, drop rule (unqualified).

**Empty result**:
A page that loaded fine but whose extraction yielded zero records: possibly genuinely empty, possibly a block the **block detector** missed. A weak, extraction-stage hint, not a verdict; it stays a CLI stderr suggestion ("retry with `--browser` / `--stealth`", the `EmptyResultAdvisor` from PR #166) and is never on its own a **block drop policy** drop. Distinct from a **blocked page**, which is a reliable load-stage classification the driver does suppress. ADR-0056 fused the two; the fusion was the bug.
_Avoid_: empty page, no-results, blocked (an empty result is not necessarily blocked).

### Escalation

**Escalating page loader**:
The one `IPageLoader` (ADR-0004), now block-aware and climbing (ADR-0083). It composes an ordered ladder of **load tier**s, the **block detector**, a **host tier floor**, and the **page cache**. For one page it starts at the host floor, loads at the current tier, and if the result is a **blocked page** with a higher real tier above it, climbs and reloads; it returns the best **page load result** it reached. The whole climb lives inside one `LoadAsync`, so the **Crawl driver**, scheduler, and **visited-link tracker** are untouched (ADR-0022 holds) and both `scrape` and `crawl` get climbing for free. It does not return the verdict — the Spider re-runs the same pure **block detector** on the returned result. Quarantine-clean: the browser / stealth tiers are injected **load transport**s from the Cdp / Playwright satellites; the only tier type core names is the BrowserNotConfigured sentinel, treated as "no real tier" so auto-escalation never launches a browser the consumer did not configure. The cache only ever holds clean loads (a blocked result is never written) and a climb bypasses it for the higher tier. `map` is not covered (the **Site mapper** uses its own HttpClient). The simpler non-climbing `PageLoader` remains for the **Agent driver** and custom `WithPageLoader` wirings. The CLI composes the ladder from flags (ADR-0083): no flag → HTTP rung + a vanilla browser rung (start at HTTP, auto-climb); `--browser` → start at the browser rung; `--stealth` → a stealth rung as the entry; `--no-auto-stealth` caps the climb at the browser rung. The entry rung is set by the start page's **PageType** (`Crawl` Static enters at HTTP, `CrawlWithBrowser` Dynamic enters at the first browser-class rung), and `WithLoadTransport` appends each Dynamic rung in registration order.
_Avoid_: retrying loader, fallback loader, escalator.

**Load tier**:
One rung of the **escalating page loader**'s ladder (`PageLoadTier`): a **load transport** paired with the **PageType** it serves. Ordered lowest to highest — HTTP (Static), then headless browser (Dynamic), then stealth (Dynamic, registered when the CLI's stealth policy includes it). The PageType tag is how the loader picks a page's starting rung — a Static page may start on any tier, a Dynamic page must skip the HTTP tier because an HTTP fetch returns un-rendered HTML — but it does not change what a transport does (a transport is its own mechanism and ignores the request's PageType). Rungs are appended by repeated `WithLoadTransport` calls, lowest first above HTTP.
_Avoid_: rung (informal), transport (that is the mechanism, one per tier), level, step.

**Host tier floor**:
The **escalating page loader**'s per-run, per-host memory of the lowest **load tier** a host's pages should start at (`HostTierFloor`). It lifts only on a high-confidence **block verdict** (a challenge-class status or header), never on a weak body-marker block, so one false-positive page cannot promote a whole legitimate host; it never lowers. Because bot protection is near-always site-wide, a status-signalling host settles at its working tier after the first confirmed block, so later same-host pages skip the doomed lower tiers (a bounded one-time climb cost per host). Resets with a fresh engine (precedent: the ADR-0050 semantic-act cache).
_Avoid_: host cache, tier map, escalation state, per-host policy.

### Wiring

**Core adapter**:
An adapter that ships in the `WebReaper` package and pulls no third-party SDK: the in-memory / file / console / CSV / JSON-lines **Sink**s and stores, the HTTP **Load transport**, the CSS and XPath **Node backend**s. The dependency-light surface a consumer gets with zero extra packages.
_Avoid_: built-in, default adapter, in-box.

**Satellite adapter**:
An adapter whose SDK is heavy or AOT-hostile, shipped in its own package (`WebReaper.Cosmos`, `WebReaper.Mongo`, `WebReaper.Redis`, `WebReaper.AzureServiceBus`, `WebReaper.Puppeteer`) and attached through the **Registration seam** by an extension-method builder that lives in that package. Core never references it; the dependency enters the consumer's graph only when they reference the satellite.
_Avoid_: plugin, optional dependency, module, add-on.

**Registration seam**:
The builder's small set of public methods over the existing role interfaces (`AddSink(IScraperSink)`, `AddScheduler(IScheduler)`, the config / cookie / visited-link / **Load transport** equivalents) through which every adapter is wired — **Core adapter**, **Satellite adapter**, or a consumer's own. The builder is a registry over role interfaces, not a factory of concrete adapters; the `WriteToX(connectionString)` convenience is sugar over this seam and lives with its **Satellite adapter**, never in core.
_Avoid_: factory, DI container, plugin host, service registration.

**Adapter warm-up**:
The one-time async work a durable adapter does before first use — a connection, a cursor restore, a `DataCleanupOnStart` wipe. It is an opt-in capability, `IAsyncInitializable` (one member, `Task InitializeAsync()`; ADR-0033): an adapter that warms up implements it; one that does not (the in-memory scheduler / tracker, `ConsoleSink`, the file **Sink**s) implements nothing — the init-side mirror of `IAsyncDisposable`, orthogonal to the role interfaces, never welded onto them. The in-process **Crawl driver** drives it: it calls `InitializeAsync` once, before the crawl loop, on every adapter it holds that has the capability (the scheduler, the **Visited-link tracker**, every **Sink**); the consumer-authored distributed driver drives its own. `InitializeAsync` is idempotent — a `Lazy<Task>` runs the work once — and the constructor does no async work; the pre-0033 `Initialization`-property-fired-from-the-constructor shape is retired.
_Avoid_: initialization, connect, startup, IAsyncInitialization (the property-shaped predecessor).

**Engine-teardown disposal chain**:
The dual of **Adapter warm-up** (ADR-0058). `ScraperEngine` is `IAsyncDisposable`; `DisposeAsync` walks adapters in *reverse* warm-up order — page processors → sinks → tracker → scheduler → spider — then runs builder-registered teardown hooks in LIFO order (`ScraperEngineBuilder.OnTeardown(IAsyncDisposable)`). The reverse order keeps dependent adapters valid while their dependencies flush; the LIFO hook list closes satellite-spawned subprocess resources (CloakBrowser's stealth Chromium, future Playwright `IBrowser`) whose lifecycle is the engine's, not the loader's. Per-adapter exceptions log at Warning and are swallowed — a scrape that succeeded is not retroactively failed by a teardown burp. The recommended consumer pattern is `await using var engine = await builder.BuildAsync();`. The ADR-0009 distributed-driver consumer disposes its own adapters by hand; the chain ships on the in-process `ScraperEngineBuilder` only.
_Avoid_: shutdown, close, finalize.

### AI-native (the ADR-0040..0069 wave)

**Markdown extraction**:
The no-schema **Content extractor** strategy (`MarkdownContentExtractor`,
ADR-0040) — the second adapter of the `IContentExtractor` seam. Reached
via the second `ICrawlSeed` terminal `AsMarkdown()`; produces a
`{ title, markdown }` `JsonObject` of the page's main content. Since
ADR-0063 the adapter is a ~20-line thin shell over the public
**HTML-to-Markdown primitive** (`HtmlToMarkdown` in
`WebReaper.Core.Markdown`) — the heuristic + strip + GFM-render
implementation lives in the primitive; the adapter delegates and
wraps the result as the `JsonObject` shape the sinks expect.
Deterministic, AOT-clean, no LLM dependency — the funnel's
"LLM-ready output without an LLM call" wedge. Callers needing just
the Markdown string (the LLM extractor's pre-clean, the change-
tracking processor's hash, the agent engine's state-building) reach
the primitive directly, not the adapter.
_Avoid_: markdown adapter, markdownifier.

**Page cache**:
The `IPageLoader`'s cache-aside collaborator (`IPageCache`, ADR-0041):
firecrawl-shaped `maxAge` TTL keyed by `(url, page-type)`. Default
`NullPageCache` (no behaviour change); `InMemoryPageCache(TimeSpan)`
the firecrawl-shaped adapter, reached via `WithMaxAge(TimeSpan)`. A
`TimeSpan.Zero` is the "store but never serve" mode used for
change-tracking and "force-fresh" crawls. A cache *write* failure is
logged and swallowed — the load succeeded.
_Avoid_: HTTP cache, response cache.

**Site mapper**:
The URL-discovery seam (`ISiteMapper`, ADR-0042) — separate from
extraction. Default `SiteMapper` reads `robots.txt` for `Sitemap:`
directives, parses each sitemap (one level of sitemap-index recursion),
extracts root-page `<a href>` URLs, unions, host-filters, optionally
substring-filters, caps at `MaxUrls`. Reached via the static
`ScraperEngineBuilder.MapAsync(url, options?)`. Best-effort: a 404 or
malformed XML at any step is logged and skipped; a partial result is
more useful than a thrown exception. Discovery is one HTTP request,
not a Crawl — routing it through the Spider pipeline would spend
visited-link / page-processor / sink budgets a discovery operation
doesn't need.
_Avoid_: sitemap parser, URL discoverer, crawler.

**Source-gen schema**:
The compile-time alternative to hand-constructed `Schema` (ADR-0045) —
the .NET-native structural differentiator per
[REPOSITIONING-PLAN §2.3](docs/REPOSITIONING-PLAN.md): mark a `partial`
class with `[ScrapeSchema]`, decorate `{ get; set; }` properties with
`[ScrapeField("selector", Type?, IsList?, Attr?)]`, and the Roslyn
generator (`WebReaper.Extraction.Generators`) emits a
`public static Schema Schema` and `public static T Materialize(JsonObject)`
on the class. CLR property types map to `DataType` automatically (`int`
→ `Integer`, etc.); `IsList=true` pairs with `List<T>`/`T[]`. Pydantic-
parity that Python's late-binding + Pydantic's reflective introspection
structurally cannot match — reflection-free, AOT-clean, IDE-visible at
compile time.
_Avoid_: schema source generator, Pydantic-port.

**Extraction router**:
The composition class (`ExtractionRouter : IContentExtractor`,
ADR-0046) — itself an `IContentExtractor`, not a seam-of-a-seam — that
composes a primary + fallback extractor with an optional validation
predicate. Default predicate is `SchemaSatisfiedValidator.IsSatisfied`:
escalates to the fallback when any required schema leaf is empty or
absent. Integer 0 / boolean false are *valid* values (legitimate data,
not missing); only string-empty triggers — matches the fold's ADR-0029
fold-output policy. Reached via `WithFallbackExtractor` / the satellite
sugar `WithLlmFallback`.
_Avoid_: failover, decorator, fallback chain.

**LLM extractor**:
The third `IContentExtractor` adapter (`LlmContentExtractor`,
ADR-0044), shipped in the `WebReaper.AI` satellite — bound to
`Microsoft.Extensions.AI.Abstractions`'s `IChatClient` (the durable
GA layer, REPOSITIONING-PLAN §2.4). The consumer brings any concrete
chat client; the extractor pre-cleans to Markdown by default (~10×
token savings), converts the **Schema** to JSON Schema via
`SchemaJsonSchemaBridge` (selectors dropped — LLMs extract
semantically), composes a system+user prompt, sets
`ResponseFormat.Json`, calls the model, parses the response. The
LLM call is the most expensive path — defaults are cheap and
deterministic (Markdown pre-clean, temperature 0, 4096-token cap,
no retries inside the extractor; the per-Job retry seam ADR-0026
covers that).
_Avoid_: AI extractor, GPT extractor.

**Prompt extraction**:
The schema-free **Content extractor** strategy (ADR-0084). The LLM reads each page and extracts per a natural-language instruction, returning whatever JSON shape fits. It is the schema-free sibling of the **LLM extractor**: same `WebReaper.AI` satellite, same `LlmCall` mechanism and Markdown pre-clean, but a free-text instruction replaces the **Schema** (the `SchemaJsonSchemaBridge` step is skipped). Surfaced as the CLI's `--prompt "<instruction>"` on `scrape` / `crawl`, it costs one LLM call per page; robust on structurally heterogeneous pages, with cost scaling by page count. That is the deliberate contrast to the **Learned-schema content extractor**'s infer-once-then-deterministic-fold path (the CLI's `--infer`).
_Avoid_: freeform extraction, instruction extractor.

**Self-healing extractor**:
The cached-selector-demotion wrapper (`SelfHealingContentExtractor`,
ADR-0047) — itself an `IContentExtractor`. On a failed deterministic
pass, calls an `ISelectorRepairer` (the new seam) for a patched Schema,
re-runs the primary with the patch, validates, and caches the patch
*if* validation succeeds. Cache key is the original Schema by reference
identity — one patch per Schema instance, the common case. The
default repairer (`LlmSelectorRepairer`, in `WebReaper.AI`) asks the
LLM for a `field-name → selector` JSON map and walks the original
Schema emitting a copy with selectors swapped. The plan §2.2's
LLM-as-proposer / fold-as-validator wedge; after the first repair,
subsequent pages run the deterministic fast path again.
_Avoid_: AI healer, self-correcting, magic-fix.

**Schema inferrer** (ADR-0067):
The schema-generation seam — `ISchemaInferrer` in
`WebReaper/Core/Parser/Abstract/`. Given a page's content and an
optional natural-language goal (`"product details"` / `"job listings"`
/ …), returns a `Schema` the deterministic fold can apply to this and
every subsequent page of the crawl. The fifth proposer-validator dock
(sibling to the **Extraction router**, **Self-healing extractor**,
**Semantic action** dispatch, and **Agent driver**) — applied to
*schema generation*, not on-page extraction. The default
implementation `LlmSchemaInferrer` ships in `WebReaper.AI`, sharing
the **LLM call** mechanism (ADR-0059) with the four existing `Llm*`
adapters; consumer-authored deterministic inferrers (heuristic,
cached, per-tenant) implement the interface directly without taking
an AI dep. Default `NullSchemaInferrer` sentinel; the builder
detects it at `BuildAsync` time when `.ExtractInferred(...)` was
called and throws with an actionable message (same pattern as
`AgentEngineBuilder`'s null-brain check). One inference per crawl —
the dock's cost story.
_Avoid_: schema generator, schema discoverer, schema autopilot.

**Learned-schema content extractor** (ADR-0067):
The first-page-infers wrapper — `LearnedSchemaContentExtractor` in
`WebReaper/Core/Parser/Concrete/`. Composes an `ISchemaInferrer` with
an inner `IContentExtractor` (typically `SchemaFold`); on the first
`ExtractAsync` call invokes the inferrer once, caches the result, and
delegates every subsequent call to the inner extractor with the
cached schema. The `SemaphoreSlim`-guarded double-checked locking
handles the `Parallel.ForEachAsync` race so parallel first-page
workers don't all pay the LLM. Per-instance cache (fresh engine =
fresh inference); `IAsyncDisposable` for the semaphore; the builder
registers it as an ADR-0058 teardown hook. Reached via the third
`ICrawlSeed` terminal `.ExtractInferred(goal?)` + the satellite
one-liner `.WithLlmSchemaInferrer(chatClient)` — same shape as the
`AsMarkdown()` + `WithLlmFallback(...)` pair.
_Avoid_: inferring extractor, AI-schema extractor, runtime-schema
extractor (the cache makes "runtime" misleading — it's first-page-only).

**Change tracking**:
The monitoring page processor (`ChangeTrackingProcessor`, ADR-0048) — a
plain `IPageProcessor` (ADR-0038) on the existing pipeline. Hashes the
page's Markdown extraction (SHA-256), compares to the previously-stored
hash for that URL via the new `IChangeStore` seam (default
`InMemoryChangeStore`), annotates the record's `JsonObject` with a
`change_status` field of `"new"` / `"same"` / `"changed"`. Markdown-
based hashing strips template noise (ad rotation / timestamps) so the
status only flips on real content change. `"removed"` is out of scope —
detecting it needs Crawl-level state outside a per-page processor.
_Avoid_: diff tracker, content monitor.

**CLI**:
The primitive agent surface (`WebReaper.Cli`, ADR-0043) — a single AOT
binary with three commands: `scrape <url>` (Markdown by default; JSON
with `--schema`), `map <url>` (URL discovery), `init` (writes the
bundled Agent **Skill** to `.claude/skills/webreaper/`). Per
[REPOSITIONING-PLAN §2.5](docs/REPOSITIONING-PLAN.md): ~35× cheaper
than MCP per token; the CLI is the wedge, Skill and MCP are adapters.
Hand-rolled `~120`-line argument parser (no `System.CommandLine` preview
deps); zero NuGet deps; `PublishAot=true` with the AotSmokeTest's IL
warning set promoted to errors.
_Avoid_: command-line tool, console app, scraper.exe.

**MCP server**:
The interop adapter (`WebReaper.Mcp`, ADR-0049) — a satellite exposing
`scrape` / `map` / `extract` as `[McpServerTool]`-decorated methods
over stdio, for MCP-only agent clients (Cursor, Claude Desktop,
Copilot Studio). Per REPOSITIONING-PLAN §2.5: interop, not the wedge.
Heavy MCP SDK deps quarantined here per the ADR-0009 satellite pattern;
core stays dependency-light + AOT-clean.
_Avoid_: MCP integration, agent server, JSON-RPC endpoint.

**Semantic action / Action resolver** (ADR-0050):
The seventh `PageAction` arm — `PageAction.SemanticAct(intent)` —
carries a natural-language intent string ("click sign in") instead of a
CSS selector. At dispatch the Puppeteer transport calls the registered
`IActionResolver` (typically the `WebReaper.AI` satellite's
`LlmActionResolver`) with the intent + the current page HTML; the
resolver returns a concrete arm (`Click` / `WaitForSelector` / `Wait` /
`EvaluateExpression`); the transport caches it per crawl by intent
string and dispatches the cached arm on every subsequent same-intent
page. Same LLM-as-proposer / deterministic-as-decider pattern as the
**Extraction router** (ADR-0046) and **Self-healing content extractor**
(ADR-0047), generalised from the extraction surface to the action
surface — the self-heal pattern stops being one feature and becomes a
project-level pattern (see [Relationships](#relationships)).
_Avoid_: `Click("sign in")` (selectors are CSS, not intents), "AI page
action", "smart click", agent step.

**Agent driver** (ADR-0051):
The sibling driver to the **Crawl driver** — page-spanning, sequential,
goal-driven. `AgentEngine` runs a `decide → persist → execute` loop:
the **Agent brain** (`IAgentBrain`) returns an **Agent decision** from
the bounded **Agent state**, the engine persists it via the
**Agent run store**, then dispatches the decision's effect — reusing
every existing seam (`IPageLoader`, `IContentExtractor`,
`IActionResolver`, sinks, processors, `IVisitedLinkTracker`). Built via
the sibling `AgentEngineBuilder.Start(url, goal)` (the ADR-0009 /
ADR-0025 two-seam pattern, third instance — beside `ScraperEngineBuilder`
and `DistributedSpiderBuilder`). Static sugar `Agent.RunAsync` /
`Agent.ResumeAsync` for the one-liner; the `WebReaper.AI` satellite's
`LlmAgentBrain` + `WithLlmBrain(chatClient)` + `LlmAgent.RunAsync(url,
goal, chatClient)` is the firecrawl-shaped AI-first surface.
_Avoid_: "scraper engine" (that's the Crawl driver), "AI scraper", "bot".

**Agent decision** (ADR-0051):
The closed sum the brain returns each step — `Extract(schema)` /
`Follow(url)` / `Act(PageAction)` / `Stop`. Every arm carries a
`Reason` for the audit-trail log. Same closed-sum lineage as
**Crawl outcome** (ADR-0001) and **Page action** (ADR-0035) — the
arm count is the documented decision (ADR-0051 fork 1: four arms, not
three; `Act` is page-local and re-invokes the brain post-action).
_Avoid_: "action", "step", "choice".

**Agent state** (ADR-0051):
The bounded view the brain sees on every `DecideAsync` call — goal,
current URL, current-page Markdown (capped), candidate `<a href>` URLs
(capped), records extracted so far, recent decisions (capped), recently
visited URLs (capped), step number. Capped by default (fork 3 verdict)
because token cost is the constraint; the caps live on
`LlmAgentBrainOptions` for the LLM brain and on `AgentEngineBuilder`
for the engine.
_Avoid_: "context", "memory".

**Agent step / Agent run** (ADR-0051):
One brain-decision-and-execute cycle is a step; a sequence of steps
from start URL until termination is a run. Termination precedence:
brain `Stop` → `MaxSteps` cap → `MaxBudgetTokens` cap → caller
cancellation. Each run has a `runId` (`AgentResult.RunId`); the
caller can resume via `Agent.ResumeAsync(runId, brain, store)` if the
store still holds the snapshot.

**Agent run store** (ADR-0051):
The fourth load-bearing piece of agent state — `IAgentRunStore` —
sibling to `IScheduler`, `IVisitedLinkTracker`, `IScraperConfigStorage`,
`ICookiesStorage`. Stores the full `AgentRunSnapshot` per `runId` so a
process restart resumes the run from `LastDecidedStep + 1`. **Persist-
before-execute**, at-least-once on effects, exactly-once on brain
decisions — the same semantics as the existing retry policy (ADR-0026),
applied to agent steps. Default `InMemoryAgentRunStore`; core also ships
`FileAgentRunStore` (JSON file per runId); satellite adapters
`RedisAgentRunStore`, `MongoAgentRunStore`, `SqliteAgentRunStore`,
`CosmosAgentRunStore` (AzureServiceBus skipped — queue-shaped doesn't
fit a snapshot store). The serialization is hand-written
(`AgentRunSnapshotCodec` + the public `WebReaperAgentJson` surface) —
AOT-trivially-safe via `Utf8JsonWriter` / `JsonObject.WriteTo`.
_Avoid_: "checkpointer", "memory store", "agent persistence".

**LLM call** (ADR-0059):
The deep mechanism module the four `Llm*` adapters share —
`LlmCall<TResponse>` in `WebReaper.AI/Llm/`. Owns the parts every
adapter would otherwise duplicate: `ChatOptions` construction,
`IChatClient.GetResponseAsync` invocation, text extraction,
code-fence stripping (one canonical implementation),
JSON-parse-failure retry (bounded at one inline retry with a
"respond with valid JSON only" reminder), `ChatResponse.Usage.
TotalTokenCount` capture, and — when the per-role descriptor
ships a tool list — the tool-call dispatch path (ADR-0060). Does
not own prompt content, response shape, or domain-type
construction; those are the **Llm call descriptor**'s policy.
Public — consumer-authored AI adapters reuse the canonical
mechanism for consistent fence-stripping / parse-retry / Usage
capture. **The mechanism is the only place** an LLM-output
post-processing decision lives; per-role adapters become thin
descriptors.
_Avoid_: LLM wrapper, LLM client (that is `IChatClient`),
prompt runner, model invoker.

**Llm call descriptor** (ADR-0059):
The per-role policy record — `LlmCallDescriptor<TResponse>`.
Carries the role's invariant `SystemPrompt`, the per-call
`BuildUserMessage` `Func<>`, the per-role `ParseResponse`
`Func<JsonElement, TResponse>`, the optional `Tools` list (the
ADR-0060 seam — when non-null the mechanism switches to tool-
call mode) and its companion `ParseToolCall`, plus per-role
defaults (`Model`, `Temperature`, `MaxResponseTokens`,
`ResponseFormat`). Composition over inheritance: every `Llm*`
adapter is a descriptor + a `LlmCall<T>` delegate. Sibling-of-
**LLM call**; one descriptor per role; each `Llm*` adapter
shrinks to ~40-60 lines of "what's my descriptor."
_Avoid_: prompt template, prompt config, role config.

**Brain tool registry** (ADR-0060):
The closed-sum-as-tools mapping the brain and the action resolver
register through `Microsoft.Extensions.AI`'s `ChatOptions.Tools`
+ `AIFunction`. Each `AgentDecision` arm (Extract / Follow /
Act\* / Stop) is one tool on the brain's registry; each concrete
`PageAction` arm (six — no `SemanticAct`, ever) is one tool on
the resolver's registry. The model picks one tool by call; the
SDK delivers the typed arguments; `LlmCall<T>`'s tool-call path
dispatches to the descriptor's `ParseToolCall`. The closed sum
becomes load-bearing **at the LLM boundary** the same way it is
in C#: an unknown arm is structurally impossible at the seam,
not a `default:` branch. Flat packaging on both registries
(post ADR-0074 the brain ships 13 flat tools — three decision
arms plus the ten flat `Act*` arms; the resolver ships 9 — the
nine concrete `Act*` arms; the `ActSemanticAct` absence is the
structural loop prevention). Both registries are **derived
views of one `PageActionArms` arm list**, each entry a
`(Descriptor, Parse)` pair, so the tool offered to the model
and the parse that decodes its call are the same list seen two
ways — the registration list and the parse dispatch cannot
drift (the pre-derivation hazard: register an arm in
`ForBrain()` but forget `ParseDecisionTool`, and the model's
call silently became `Stop`). Resolver's "must not return
SemanticAct" rule is enforced structurally by SemanticAct
exposing **no resolver adapter** — an arm with no resolver
`Parse` cannot appear in a registry derived from the arm list,
so the omission is a compile-time absence, not a runtime
`.Where(x != SemanticAct)` filter or a hand-maintained second
list. The `_ => null` (resolver) / `_ => Stop` (brain) fallback
remains, but now catches only a genuinely hallucinated tool
name, never a wiring omission.
_Avoid_: tool list (unqualified — there are two distinct
registries), function registry, brain commands.

**Agent decision outcome** (ADR-0061):
The closed-sum companion to **Agent decision** — `AgentDecisionOutcome`
on `WebReaper.Domain.Agent`. The brain's feedback signal:
"what happened when the engine executed your last decision?"
Six arms — `None` (first step), `Extracted(Record?, RecordCount)`,
`Followed(ActualUrl, StatusCode)`, `ActDispatched(ResolvedAction)`,
`Failed(Reason, ExceptionType?)`, `Stopped(Reason)` (end-state
marker; brain never sees it). Carried on `AgentState.LastOutcome`
(default `None`) and persisted on `AgentRunSnapshot.LastOutcome`
so resumed runs pick up causally. Same closed-sum lineage as
`AgentDecision` itself (ADR-0001 / ADR-0051). On Extract,
the validator's verdict (ADR-0062) becomes
`Failed("validation: <reason>", null)` — the brain reads the
specific failure and revises. Load failures stop being terminal
(ADR-0051's behaviour change): they become `Failed(...)` and
the loop continues; the brain decides whether to retry / pivot /
Stop.
_Avoid_: agent feedback, agent result-of-step, decision result.

**Schema validator** (ADR-0062):
The validator half of the proposer-validator pattern —
`ISchemaValidator` in `WebReaper/Core/Parser/Abstract/`. Sibling
seam to the **Content extractor**. One method:
`ValidationResult Validate(JsonObject? extracted, Schema? schema)`.
Default `SchemaSatisfiedValidator` preserves the ADR-0029 +
ADR-0046 policy (string-empty / list-empty triggers; integer 0,
boolean false are valid; missing field path surfaces in
`Reason`). Three sites consume it: the **Extraction router**
(ADR-0046, primary-to-fallback decision); the **Self-healing
content extractor** (ADR-0047, repair trigger — the
`ISelectorRepairer.RepairAsync` signature widens to carry the
`failureReason` string the validator emitted); and the **Agent
brain** (ADR-0051), through the agent driver's Extract switch
arm — a failed verdict becomes `AgentDecisionOutcome.Failed
("validation: ...")` (ADR-0061 composition). Selected via
`WithSchemaValidator(...)` on both builders.
_Avoid_: schema checker, extraction validator, content validator.

**HTML-to-Markdown primitive** (ADR-0063):
The pure synchronous conversion function — `HtmlToMarkdown` in
`WebReaper.Core.Markdown`. Two overloads: `Convert(html) →
string` for the just-Markdown callers (the LLM extractor's pre-
clean, the LLM repairer's pre-clean, the change-tracking hash,
the agent engine's state-building); `ExtractMainContent(html) →
MainContent { Title, Markdown }` for the adapter case. The
canonical tag-based Readability heuristic (article → main →
[role=main] → body), the chrome-strip list, the GFM rendering
all live here. `MarkdownContentExtractor` becomes a ~20-line
thin `IContentExtractor` shell over the primitive — the
adapter survives because the `AsMarkdown()` seed terminal needs
the seam-implementing class, but its body is "one call, one
wrap." Public; AOT-clean.
_Avoid_: Markdown converter (use the function name), GFM
renderer, content-to-markdown.

**AI policy** (ADR-0064):
The one-shot `UseAi(IChatClient, AiOptions?)` registration on
both builders — `ScraperEngineBuilder` and `AgentEngineBuilder`.
Aggregates the five `WithLlm*` registrations into one method;
mode-gated (the **AI policy mode** enum picks the canned
configuration). Per-role overrides via nested options records:
global `Model` / `Temperature` / `MaxResponseTokens` flow down;
per-role options-record fields override on conflict. The à la
carte `WithLlm*` methods stay; `.UseAi(...)` is sugar over them.
On the agent side `.UseAi(...)` always wires the brain (the
agent is structurally useless without one — ADR-0051); on the
scraper side `.UseAi(Recommended)` wires the firecrawl-shaped
fall-back-first triple (LLM-as-fallback extractor +
self-healing repairer + action resolver).
_Avoid_: AI config, AI setup, AI bundle.

**AI policy mode** (ADR-0064, extended by ADR-0068):
The closed enum picking the canned `.UseAi(...)` configuration —
`AiPolicyMode { Recommended, LlmPrimary, ExtractionOnly, None,
Inferred }`. Mutually exclusive (not flags — composition is what
à la carte is for). `Recommended` is the default: deterministic-
first + LLM-as-rescue (matches ADR-0046 / ADR-0047 structural
posture). `LlmPrimary` replaces the deterministic extractor with
the LLM on every page. `ExtractionOnly` wires just the extractor
+ (on scraper) fallback. `None` is the explicit escape — wires
the brain only on agent-side, nothing on scraper-side. `Inferred`
(ADR-0068) wires the schema inferrer + action resolver — the
scraper-side companion to `.ExtractInferred(goal?)`; mutually
exclusive with the LLM-fallback / LLM-primary modes (both
register an `IContentExtractor` that would shadow the
`LearnedSchemaContentExtractor` wrapper). On the agent builder,
`Inferred` throws actionably (the brain proposes its own schemas
per Extract decision; an external inferrer is structurally
redundant).
_Avoid_: policy flags, AI features, configuration preset.

**Cache policy** (ADR-0065):
The per-role system-prompt caching policy — `CachePolicy { Default,
Hinted }` in `WebReaper.AI.Llm`. The **LLM call** mechanism
(ADR-0059) encodes `Hinted` as the Anthropic-standard
`cache_control: { type: "ephemeral" }` hint on the system
`ChatMessage.AdditionalProperties` (M.E.AI 9.4 surface); providers
that don't recognise the key ignore it silently. Default in
`AiOptions.CachePolicy` is `Hinted` — the AI-native cheap-default
ethos (Anthropic users get ~5–10× cheaper system prompts;
OpenAI users see no change because the OpenAI auto-cache
continues regardless of the hint). Per-role `LlmExtractorOptions.
CachePolicy` / `LlmActionResolverOptions.CachePolicy` /
`LlmAgentBrainOptions.CachePolicy` are nullable (null = inherit
from `AiOptions.CachePolicy` via the `Resolve*` helpers when
wired through `.UseAi(...)`; null = `CachePolicy.Default` when
the adapter is constructed à la carte). `LlmCallResult` carries
the cached-vs-uncached split — `InputTokens` / `OutputTokens` /
`CachedInputTokens` / `TotalTokens` — read from
`UsageDetails.AdditionalCounts` for the known provider keys
(`cached_input_tokens`, `cache_read_input_tokens`,
`InputTokenCount.Cached`, `prompt_tokens_details.cached_tokens`).
_Avoid_: cache mode, prompt cache (the hint is a per-call
metadata write, not a separate cache).

**LLM call telemetry** (ADR-0066):
The thread-safe accumulator the **LLM call** mechanism reports
completed calls into — `ILlmCallTelemetry` seam in
`WebReaper.AI.Llm` (default `LlmCallTelemetry`; `Interlocked`
on aggregates + `ConcurrentDictionary` for per-adapter). One
instance per engine via the builder-side `TelemetryHooks` hook;
the satellite's `BuilderTelemetryExtensions` materialises it on
first `WithLlm*` / `.UseAi(...)` call (a per-builder
`ConditionalWeakTable` lookup — multiple registrations on one
builder share one accumulator). Per-adapter attribution keyed by
**Llm call descriptor**'s `Name`. `Snapshot()` returns immutable
`LlmTelemetrySnapshot` (aggregate totals + `PerAdapter` dict of
`LlmAdapterStats`); `Reset()` clears (called by the engine at
the start of each `RunAsync` to isolate runs). Null-token
sentinel logic distinguishes "no call surfaced a value"
(snapshot field null) from "some calls reported 0" (snapshot
field 0). Consumer-authored telemetry implementations are
valid — wire via the builder's `TelemetryHooks` property
directly.
_Avoid_: LLM usage tracker, cost meter, AI metrics.

**Run report** (ADR-0066):
The per-run telemetry summary — `RunReport(object? Llm,
TimeSpan Duration)` in `WebReaper.Domain.Telemetry` (core).
Returned by `ScraperEngine.RunAsync` (the engine's return type
widened `Task → Task<RunReport>` in v10.0.0 — pre-tag breaking
change; `await engine.RunAsync(ct)` discard semantics keeps
working) and exposed via `AgentResult.Report` (the record's
positional shape evolves 6 → 7 fields). `Llm` is `object?` to
keep the ADR-0009 satellite quarantine — consumers cast to
`WebReaper.AI.Llm.LlmTelemetrySnapshot` when the AI satellite is
in use; `null` when no LLM adapter ran on the engine. Sibling
type `RunTelemetryHooks(Func<object?> Snapshot, Action Reset,
Func<long?>? TotalLlmTokens = null)` is the satellite-clean
callback channel into the engine ctors; the satellite constructs
it once at `BuildAsync` time, the engine calls `Reset` at
`RunAsync` entry and `Snapshot` at exit (and `TotalLlmTokens`
between steps on the agent path for the `MaxBudgetTokens`
check).
_Avoid_: telemetry result, AI report, usage report.

## Relationships

- A **Crawl** processes many **Job**s.
- A **Job** carries a **Selector chain**; the chain's length and head derive the **Page category** (it is not stored on the Job).
- A **Crawl step** maps one **Job** to one **Crawl outcome**.
- A **Target page** outcome produces one **ParsedData**; the **Crawl driver** runs it through the **Page processor** pipeline, then emits the surviving record to every **Sink**.
- A file **Sink** is one **File sink drain** plus one **Row format**; the drain is shared, the format is the only per-format variation.
- A **Transit page** **advance**s the selector chain; a **Page with pagination** **retain**s it.
- A **Content extractor** turns a **Target page** into one **ParsedData**; the **Schema fold** is its one core adapter (an alternative extraction strategy — e.g. LLM-backed — is another adapter of the same seam).
- A **Schema fold** interprets a **Schema** by calling one **Node backend**; the backend yields **raw value**s, the fold applies **typed coercion**.
- A **Payload shell** serializes one payload and delegates storage to one **Keyed blob store**; the store never knows which payload it holds.
- A file **Keyed blob store**, file **Scheduler**, file **Visited-link tracker**, and **File sink drain** each keep their own write/read essence and share only **File persistence prep** (eager directory creation, deterministic cleanup-on-start, missing-file-as-empty). Concurrent durable append is a single-writer concern (the **File sink drain**'s one-consumer pattern), deliberately not a shared lock; the **Scheduler**'s resumable cursor + position file stay role-local.
- The **Spider** calls one **Page loader** with a **PageRequest**; the loader dispatches on **PageType** to one **Load transport**, which applies the optional proxy its own way.
- The builder wires every adapter — **Core adapter**, **Satellite adapter**, or consumer-supplied — through the **Registration seam** over a role interface; it never constructs a **Satellite adapter** itself.
- A **Crawl driver** drives a **Crawl**: it seeds a **Job** per start URL, schedules **Job**s, owns the **Visited-link tracker** and the stop rule, and interprets each **Job report**.
- The **Crawl driver** holds one **Spider**; per **Job** it calls the **Spider**, receives a **Job report**, applies the **Visited-link tracker** test-and-set (the idempotency authority), registers the **Job** with the **Stop rule** (which credits the discovered children through the **Outstanding-work latch** in one atomic step and checks the page limit) before enqueueing the surviving child **Job**s, runs the **ParsedData** through the **Page processor** pipeline, and fans the surviving record out to every **Sink**.
- The **Spider** loads the page, runs the **Crawl step**, and returns a **Job report**; it no longer touches the **Visited-link tracker**, the limit, the **Sink**s, or config storage — its headless flag and parsing **Schema** are supplied at construction.
- The **Outstanding-work latch** trips once when credit reaches zero; its correctness rests on the **Visited-link tracker**'s atomic test-and-set, not on queue delivery semantics. The in-process and distributed **Crawl driver**s are its two adapters — the in-process driver reaches it through the **Stop rule**, the distributed driver consults the latch primitive directly.
- The in-process **Crawl driver** consults one **Stop rule**, which composes the **Outstanding-work latch** (completion) and the soft page limit (cutoff) into the single stop verdict; completion and cutoff are distinct mechanisms, composed, never merged.
- The in-process **Crawl driver** holds one **Retry policy** and runs every per-Job **Spider** call through `IRetryPolicy.ExecuteAsync`; the policy bounds attempts and never retries cancellation. The distributed-worker reduced shell holds no **Retry policy** — its retry boundary is the queue's redelivery.
- A **Semantic action** (`PageAction.SemanticAct(intent)`) dispatches through one **Action resolver**: cache miss ⇒ call the resolver with the rendered HTML, receive a concrete arm (Click / WaitForSelector / Wait / EvaluateExpression), dispatch it, cache by intent; cache hit ⇒ dispatch the cached arm; cached-arm dispatch failure ⇒ invalidate + re-resolve. The deterministic path is the hot path — first page pays the LLM, every subsequent same-intent page dispatches the cached arm with no LLM call. Same proposer-validator shape as the **Extraction router** and **Self-healing content extractor** on the extraction surface (ADR-0046, ADR-0047).
- An **Agent driver** drives an **Agent run**: it loads the start URL, asks the **Agent brain** for an **Agent decision** from the **Agent state**, persists via the **Agent run store** (persist-before-execute), dispatches the decision's effect using the same seams the **Crawl driver** uses (`IPageLoader`, `IContentExtractor`, `IActionResolver`, `IScraperSink`, `IPageProcessor`, `IVisitedLinkTracker`), and loops until the brain returns `Stop` or a cap fires.
- The five proposer-validator docks — **Extraction router** (ADR-0046), **Self-healing content extractor** (ADR-0047), **Semantic action** dispatch (ADR-0050), **Agent driver** page-selection (ADR-0051), and **Schema inferrer** + **Learned-schema content extractor** (ADR-0067) — share one shape: an LLM proposer is asked once, a deterministic validator decides, the cached deterministic answer is the hot path. The first three are page-local; the fourth is page-spanning; the fifth is *crawl-spanning* (one inference per crawl, then deterministic fold forever after). The pattern is now a project-level invariant, not features that happen to look alike.
- The third `ICrawlSeed` terminal `.ExtractInferred(goal?)` (ADR-0067) is the schema-generation entry point — sibling to `.Extract(schema)` (deterministic fold over a hand-authored schema) and `.AsMarkdown()` (no-schema Markdown). Marks the builder; `BuildAsync` resolves the marker by wrapping the registered content extractor with the **Learned-schema content extractor** and consulting the registered **Schema inferrer** (or throwing actionably when the inferrer is still the null sentinel). The inferred schema is per-engine: a fresh build = a fresh inference; consecutive `RunAsync` calls on the same engine reuse. Single-host crawl is the v1 assumption (no per-host cache); validator-driven re-inference is a v2 deferral.
- Every `Llm*` adapter in `WebReaper.AI` is a **Llm call descriptor** composed with one **LLM call** mechanism — `LlmContentExtractor` / `LlmSelectorRepairer` / `LlmActionResolver` / `LlmAgentBrain` are all "what's my descriptor" classes around a shared `LlmCall<TResponse>`. The mechanism owns code-fence stripping, JSON-parse-failure retry, `ChatResponse.Usage` capture, and the tool-call dispatch path; the descriptor owns prompt content + response shape + per-role failure policy. Consumer-authored AI adapters reuse the mechanism for consistent behaviour.
- The **Brain tool registry** (ADR-0060) is one **Llm call descriptor**'s `Tools` field — when set, `LlmCall<T>` takes the tool-call path instead of JSON-mode parsing. The closed sum (`AgentDecision` arms on the brain; concrete `PageAction` arms on the resolver) becomes load-bearing at the LLM boundary the same way it is in C#; the resolver's "must not return SemanticAct" rule is enforced by the absence of an `ActSemanticAct` tool, not by runtime validation.
- The brain's view of the previous step is one closed-sum field — `AgentState.LastOutcome`, an **Agent decision outcome**. The engine populates it from the prior step's execution result; first-step brains see `None`. The validator's verdict on Extract (a failed **Schema validator** check) surfaces as `Failed("validation: <reason>", null)`; load failures surface as `Failed(...)` and the loop continues; SemanticAct's resolution becomes `ActDispatched(ResolvedAction)`. The brain reads the previous outcome alongside its history and decides next.
- The **Schema validator** seam (ADR-0062) is consumed by three sites — the **Extraction router** (primary-to-fallback decision), the **Self-healing content extractor** (repair trigger + failure-reason propagation to `ISelectorRepairer.RepairAsync`), and the **Agent driver** (Extract switch arm; verdict becomes the **Agent decision outcome**). The default `SchemaSatisfiedValidator` preserves the ADR-0029 / ADR-0046 policy; consumers swap via `WithSchemaValidator(...)` on either builder.
- The **HTML-to-Markdown primitive** (ADR-0063) is a public static function shared by **Markdown extraction** (the adapter is a thin shell), the **LLM extractor**'s pre-clean, the **Self-healing extractor**'s pre-clean, the **Change tracking** processor's hash, and the **Agent driver**'s state-building. One canonical function in `WebReaper.Core.Markdown`; the adapter survives only because the `AsMarkdown()` seed terminal needs the seam-implementing class.
- **AI policy** (`.UseAi(client, opts?)`, ADR-0064; extended by ADR-0068) is the one-line wiring that aggregates the `WithLlm*` registrations. The **AI policy mode** enum (`Recommended` / `LlmPrimary` / `ExtractionOnly` / `None` / `Inferred`) picks the canned configuration; per-role nested options (including `Inferrer: LlmSchemaInferrerOptions?` since ADR-0068) override the global defaults. On the agent builder `.UseAi(...)` always wires the brain (`Recommended` / `LlmPrimary` / `ExtractionOnly` differ only in what *else* gets wired; `Inferred` throws actionably — the brain is the agent's own inferrer); on the scraper builder `.UseAi(Recommended)` is the firecrawl-shaped fall-back-first triple, and `.UseAi(Inferred)` is the schema-inference companion to `.ExtractInferred(goal?)`. À la carte `WithLlm*` methods remain for fine-tuning.
- The **Schema validator** seam (ADR-0062) now has four consumer sites — the three named in ADR-0062 (**Extraction router**, **Self-healing content extractor**, **Agent driver**) plus the **Learned-schema content extractor** (ADR-0069). The wrapper consults the registered validator on every inner-extractor output; N consecutive invalid verdicts drop the cached inferred schema and trigger re-inference on the next call. The cost-cap argument (`MaxReInferencesPerInstance`) bounds total LLM spend on a single instance. Default `ReInferAfterFailures = 3` via the satellite's `LlmSchemaInferrerOptions` — opt-out by setting it to `0` (preserves ADR-0067 v1 trust-the-cache).
- The **Cache policy** (ADR-0065) is one more field on the **Llm call descriptor** alongside `Tools` / `ParseToolCall`; the **LLM call** mechanism writes the Anthropic-standard `cache_control` hint to the outbound system `ChatMessage.AdditionalProperties` when `Hinted`. Default in `AiOptions.CachePolicy` is `Hinted` (the cheap-default ethos); per-role nullable `CachePolicy?` flows through the `Resolve*` helpers (null = inherit global). `LlmCallResult` grows the cached-vs-uncached split — `CachedInputTokens` reads from `UsageDetails.AdditionalCounts` for known provider keys. Anthropic users get ~5–10× cheaper system prompts; OpenAI users see no change (auto-cache continues); Gemini / local-model users see the hint ignored.
- The **LLM call** mechanism (ADR-0059) reports each completed call to one **LLM call telemetry** accumulator (ADR-0066) — `LlmCall<T>`'s ctor takes an optional `ILlmCallTelemetry?`; the four `Llm*` adapters thread one in from the builder via the satellite's `BuilderTelemetryExtensions` (the `ConditionalWeakTable` per-builder lookup). Multiple `WithLlm*` calls on one builder share one accumulator. The engine consumes the accumulator's snapshot through the AI-clean `RunTelemetryHooks` record — `ScraperEngine.RunAsync` returns a **Run report** (`Task → Task<RunReport>` — pre-tag breaking change, discard semantics keeps `await engine.RunAsync(ct)` working); `AgentResult.Report` carries the same record (the result's positional shape evolves 6 → 7). The agent engine's `MaxBudgetTokens` cap (widened `int? → long?`) is finally enforced inside the loop via the hooks' `TotalLlmTokens` getter — termination precedence is `Stop → MaxSteps → MaxBudgetTokens → cancellation`.

## Example dialogue

> **Dev:** "When a page-with-pagination crawl runs, do the item Jobs and the next-page Jobs both keep the same selector chain?"
> **Domain expert:** "No. The item Jobs **advance** — the listing selector is consumed, so they're target pages now. The next-page Jobs **retain** the chain, because page 2 of the listing is the same step, not a deeper one."

> **Dev:** "The Mongo config store used to keep a queryable BSON document; now it's a string blob — isn't that a regression?"
> **Domain expert:** "No. WebReaper only ever fetches a whole config by key, never queries inside it. The **keyed blob store** holds an opaque string; the `System.Text.Json` source-gen converters that round-trip the `PageAction` closed sum (ADR-0035) and the `ImmutableQueue` selector chain live in the config **payload shell**, not the store (ADR-0008's `$kind`/kind-tagged grammar, formerly `TypeNameHandling.Auto`). The BSON shape was never load-bearing."

> **Dev:** "How does the Spider decide between an HTTP fetch and a headless browser?"
> **Domain expert:** "It doesn't — it builds a **PageRequest** and hands it to the one **page loader**. The loader reads **PageType** and dispatches to the HTTP or browser **load transport**. Whether a proxy is used, and how it's applied, is the transport's business, not the Spider's."

## Flagged ambiguities

- **Selector-chain handling of pagination vs following was implicit.** In `Spider.CrawlAsync` one call site passed the dequeued chain and another the original chain, with nothing naming the difference. Resolved structurally: the **Crawl step** returns a **Crawl outcome** whose `Followed.Next` and `Paginated.Items` carry the **advance**d chain and whose `Paginated.NextPages` carries the **retain**ed chain — the two rules are now distinct named fields, not two look-alike call sites (see `docs/adr/0001-crawl-outcome-closed-sum.md`).
- **"Page type" vs "page category".** Resolved: **page category** = Target / Transit / Pagination, derived from the selector chain. **PageType** is the load mode (Static vs Dynamic, i.e. HTTP vs Puppeteer). Distinct concepts — never conflate them.
- **The HTML-vs-JSON untyped-leaf difference was accidental duplication; it is now a deliberate, pinned property.** An untyped leaf is the **raw value** verbatim: HTML yields a string, JSON keeps its native number/bool. This is intentional (JSON-endpoint users depend on native types) and is the *only* legitimate behavioural difference between backends — it rides on `ExtractRaw`'s return type, not copied code, and is pinned cross-backend in `SchemaFoldTests`. Do not "unify" it (see `docs/adr/0002-schema-fold-and-node-backend-seam.md`).
- **Previously-divergent log/selector behaviour is now uniform — by design, not regression.** The missing-node and parsing-error log messages were textually different per backend, and the HTML single-value path tolerated a missing selector where the list paths did not. The fold makes all three uniform. Observable outcomes (field left empty/unset, parse not aborted) are unchanged; only the divergent log text and the single-value selector-miss mechanism were unified.
- **The config/cookie persistence stores were eight near-duplicate classes; the duplication had drifted into real bugs. Now one keyed blob store + per-payload shells — deliberate, not regression.** Mongo stores an opaque `{id, blob}`, not a queried BSON projection (never queried — do not "restore" it); the missing-value policy is uniform (`null` ⇔ absent at the store; the config shell throws a typed not-found, the cookie shell returns an empty `CookieContainer`), replacing the File adapter's `NullReferenceException` and the silent-null divergence; `PutAsync` is upsert-by-key, fixing the Mongo append/read-oldest bug; `ScraperConfig` round-trips with full type fidelity through *every* backend (Redis was silently lossy; ADR-0003 used `TypeNameHandling.Auto` here, itself later retired by ADR-0008 for the `System.Text.Json` source-gen grammar); in-memory storage now round-trips through the shell's serializer like every other backend (was: held the live object), so the cheap path exercises the same serialization. `RedisBase`'s process-static single-multiplexer bug is fully resolved: `RedisBase` is retired and all four Redis adapters (blob store, scheduler, sink, visited-link tracker) share one `RedisConnectionPool` — one multiplexer per connection string, no statics. See `docs/adr/0003-keyed-blob-store-and-payload-shells.md` and `docs/adr/0005-redis-connection-pool.md`.
- **The Static/Dynamic loader split was two single-adapter seams plus a copy-pasted requester triad and Puppeteer pair; the proxy/no-proxy choice had no home. Now one `IPageLoader` + two `IPageLoadTransport`s — breaking, deliberate.** `IStaticPageLoader` had exactly one implementation; the proxy decision was re-made in the builder branch, the requester triad, and the Puppeteer pair, with bugs drifted into the copies. The Spider no longer dispatches by load mode (that home moved behind the **page loader**); `IStaticPageLoader`, `IBrowserPageLoader`, `IPageRequester` + its three impls, and the two Puppeteer classes are removed (major SemVer). Fixed by construction, deliberately: the non-proxy static path now actually applies stored cookies (the handler was previously built *before* the cookie container was set); one canonical User-Agent (the triad had two by copy-drift); one canonical browser navigation wait, `Networkidle2` (the Puppeteer pair had `DOMContentLoaded` vs `Networkidle2` by accidental drift); the never-constructed, buggy `ProxyPageRequester` is gone. Out of scope, preserved as-is: the browser page-action table still handles only four of six `PageActionType`s (a missing feature, not this duplication deepening). See `docs/adr/0004-one-page-loader-transport-seam.md`. **Update:** that gap is closed — ADR-0035 makes `PageAction` a closed sum of typed arms (the `PageActionType` enum and the untyped `object[]` removed), so the transport dispatches with a `switch` over the arms and `WaitForSelector` / `EvaluateExpression` are wired; a `PageActionType`-keyed dictionary that could silently lack an entry no longer exists. See `docs/adr/0035-pageaction-closed-sum.md`.
- **The distributed scheduler's `Job` round-trip is serialize/deserialize-asymmetric — named, not yet fixed.** `RedisScheduler` writes a `Job` with `TypeNameHandling.None` and reads it back with default settings, so a `Job`'s `ImmutableQueue<LinkPathSelector>` and `PageAction.Parameters` (`object[]`) lose the type metadata they need to rematerialise — the same asymmetry ADR 0003 fixed for the config payload. The `RedisBase` retirement (ADR 0005) preserved this verbatim rather than widen its scope; it is a distinct future candidate, not a regression introduced there. **Update:** that candidate is decided — ADR 0008 retires `TypeNameHandling` entirely for a `System.Text.Json` source-gen + converters grammar, and `RedisScheduler` serialises/deserialises a `Job` through the *same* context as the config payload, so the asymmetry becomes unrepresentable (there is no `TypeNameHandling` knob to set differently on the two sides). Decided and Phase-0-spike-proven; landed staged, not big-bang. See `docs/adr/0005-redis-connection-pool.md` and `docs/adr/0008-system-text-json-typed-pipeline.md`.
- **The two file sinks were one buffered drain copy-pasted with drifted bugs; now one `BufferedFileSink` + an `IFileSinkFormat` quirk — deliberate, not regression.** Cleanup and directory creation are now eager and unconditional (deterministic even for a zero-row crawl; old CSV kept stale data and never created the directory, old JSON-lines created it only when cleaning); one consumer is started once, bound to the first emit's token (old JSON-lines used `CancellationToken.None` with dead re-init code, old CSV could double-spawn it under concurrent first emits). Observable file content is unchanged. Out of scope, preserved verbatim: one `File.AppendAllTextAsync` per row and no consumer flush/dispose — a *shared* property of the old code, a separate future candidate. See `docs/adr/0006-file-sink-buffered-drain.md`.
- **XPath is a third `ISchemaBackend`, and it deliberately does not copy the CSS backend's `src`→`title` rewrite — by design, not inconsistency.** Discussion #17 asked for XPath/RegEx; XPath shipped as `AngleSharpXPathSchemaBackend` over the same AngleSharp DOM (the ADR 0002 seam used as intended, fold unduplicated). The CSS backend's requested-`src`→`title` rewrite is a quarantined legacy quirk of *that* backend; per ADR 0002 quirks are backend-local, so the XPath backend returns the attribute asked for (the only behavioural difference, pinned by a test). RegEx selectors were declined: a regex over markup has no node scope and cannot satisfy the `SelectMany`/`SelectOne` contract. Link discovery stays CSS (out of scope, as with the JSON backend). See `docs/adr/0007-xpath-schema-backend.md`. **Update:** the *attribute-vs-text-vs-html dispatch* that both AngleSharp backends shared verbatim (the markup-leaf grammar, distinct from the `src`→`title` quirk) is now one home — `AngleSharpRawExtractor` (Tier-2 internal static) — and each AngleSharp backend's `ExtractRaw` shrinks to "apply this backend's quirks, then delegate." The seam `ISchemaBackend<TNode>` is unchanged; the JSON backend is unchanged (its grammar is a `JsonNode.DeepClone()`, structurally different from markup). ADR-0007's pinned behavioural difference still stands — the CSS backend applies the rewrite before delegation, the XPath backend does not — but now structurally, not by code duplication. See `docs/adr/0027-anglesharp-raw-extractor.md`.
- **The AngleSharp-DOM markup-leaf grammar lived as a 7-line `if/else if/else` dispatch copy-pasted across both AngleSharp backends; now one internal static helper. Internal-only refactor, deliberate.** `AngleSharpSchemaBackend.ExtractRaw` and `AngleSharpXPathSchemaBackend.ExtractRaw` shared the same three-arm dispatch (attribute / inner-HTML / text against an `IElement`) with the CSS `src`→`title` rewrite as the only legitimate difference — a misapplication of ADR-0002's "quirks are backend-local," because the dispatch itself is the **Raw value** grammar (driven by `SchemaElement.Attr` / `SchemaElement.GetHtml`), not a quirk. A third AngleSharp-DOM backend (Fizzler, …) would re-derive the dispatch and drift. Resolved: `AngleSharpRawExtractor.ExtractRaw(IElement, SchemaElement)` is the one home; the CSS backend's `ExtractRaw` is the `src`→`title` mutation + a one-line delegation; the XPath backend's is a one-line expression-bodied delegation. JSON backend untouched (its grammar is structurally different — a `JsonNode.DeepClone()` preserving native value-kind, the ADR-0002 untyped-leaf raw-value pin). `ISchemaBackend<TNode>` seam unchanged; consumer / satellite backends see no public-surface change. Rejected with load-bearing reasons so future reviews don't re-suggest them: pushing the dispatch into the fold (forces the JSON backend to implement three meaningless methods and breaks the ADR-0007 quirk quarantine); an abstract base class the two AngleSharp backends extend (the backends already diverge on `SelectMany`/`SelectOne` shape — the base would carry nothing else); two copies pinned by test (catches drift after the fact, not by construction); generalising the helper to a third method on `ISchemaBackend<TNode>` (broad-seam-over-narrow-pattern; the JSON backend would not use it). See `docs/adr/0027-anglesharp-raw-extractor.md`.
- **Every third-party-SDK adapter was a hard core `PackageReference` wired by the builder's concrete `WriteToX` methods; now the builder is a public registration seam and the heavy adapters are per-technology satellite packages — breaking, deliberate.** `ScraperEngineBuilder` statically `new`d ~11 concrete adapters, forcing `Microsoft.Azure.Cosmos` (→ Newtonsoft + native `ServiceInterop`), `MongoDB.Driver` (→ the suppressed `SharpCompress` CVE), `StackExchange.Redis`, `Azure.Messaging.ServiceBus`, and `PuppeteerSharp` (→ Chromium) into every consumer's graph, including a plain HTTP→file crawl. The seam interfaces were already deep (ADR 0003/0004/0008) and five of six builder registration methods already existed; the deepening is mostly deletion plus one `WithLoadTransport`. `CosmosSink`/`MongoDbSink`/the Redis family/`AzureServiceBusScheduler`/`BrowserPageLoadTransport` move to `WebReaper.Cosmos`/`.Mongo`/`.Redis`/`.AzureServiceBus`/`.Puppeteer` and re-attach as extension methods over the public **Registration seam**; `SpiderBuilder`'s duplicate public adapter surface is removed and it is made `internal`; the default **Page loader** is HTTP-only (`GetWithBrowser` is opt-in via `WebReaper.Puppeteer`). Clean cut, no compat forwarder — a forwarder still `new`s the adapter so core would still reference the package, defeating the dependency-light core; a deliberate departure from the ADR 0002/0003/0004/0008 staged-compat precedent. Newtonsoft still does not leave core (the JSON-backend JSONPath cursor and `CookieStore`, ADR 0008 follow-ups, are untouched); zero-warning core `PublishAot` remains gated on ADR 0008's named JSONPath migration. See `docs/adr/0009-registration-seam-and-satellite-adapters.md`. **Update:** that candidate is decided and shipped (2026-05-17). The JSON-backend JSONPath cursor was migrated to an **in-repo JSONPath-subset evaluator over `System.Text.Json.Nodes.JsonNode`** — deliberately *not* a JSONPath dependency (a new core dep would contradict this bullet's own dependency-light result; the only dialect `Schema` drives — optional `$`/`$.` root, `.`-separated property segments, a trailing `[*]` array wildcard — is small and fully pinned by the JSON test corpus, and an RFC-9535 library would reject WebReaper's relative non-`$` selectors anyway). `CookieStore` needed no migration: it has been on the `WebReaperJson` System.Text.Json source-gen over a flat `CookieDto` since 6.0.0 — the "untouched" / ADR-0008 "preserved verbatim" prose was itself stale (the same docs-follow-code lag class as ADR-0008's own post-release corrections). Core therefore has zero Newtonsoft code reach: the `Newtonsoft.Json` `PackageReference` is dropped from `WebReaper.csproj` and the *whole* core (not the scoped Newtonsoft-free path ADR 0008 could originally promise) publishes Native-AOT zero-warning, proven by `WebReaper.AotSmokeTest` extended to drive the JSON backend (RED `IL2104`/`IL3053` Newtonsoft rollup before, green after). The only Newtonsoft left in the graph is `WebReaper.Cosmos`'s `CosmosSink` via the Cosmos SDK — the satellite, off the core graph by this bullet's own decision, deliberately not `IsAotCompatible`. See `docs/adr/0008-system-text-json-typed-pipeline.md` ("JSONPath follow-up closed (2026-05-17)").
- **Four file-backed adapters hand-rolled the same pre-write file handling and drifted into three single-copy bugs; the fix is one small stateless prep helper — not a held-handle substrate.** `FileBlobStore` created no directory (threw on any nested path, contradicting its own "the key *is* the file path" doc), `FileVisitedLinkedTracker` created it only when the file was absent (threw if the file existed but its directory had since been removed), only `FileScheduler`/`BufferedFileSink` did it eagerly and unconditionally; `DataCleanupOnStart` timing diverged the same way — the ADR-0002/0003/0006 "copies drifted into bugs that exist in only one copy" shape, in the file-backed cluster ADR 0003 scoped out ("a set store, a queue, an append sink"). The genuinely shared, bug-prone part — eager unconditional directory creation, deterministic cleanup-on-start, missing-file-as-empty — moves to one stateless **File persistence prep** helper. An earlier draft proposed a stateful *Durable file substrate* (one held write handle per path, write-through flush, a shared single-writer lock) that additionally **superseded ADR 0006's deferred open/close-churn fence**; rejected on .NET-idiom grounds — idiomatic concurrent durable append in .NET is a single-writer funnel (a `Channel`/`BlockingCollection` + one consumer, exactly `BufferedFileSink`'s existing drain), not a shared `SemaphoreSlim` per write, so a lock-substrate would standardize the *weaker* pattern and force the one idiomatic adapter to opt out; and the held handle re-opened the lifetime scope ADR 0005/0006 bounded out. **ADR 0006's fence stands** (per-row open/close is still its own deferred candidate, not closed here); the earlier draft's "`FileBlobStore` replace becomes serialised" consequence is **withdrawn** — no shared lock is introduced, so no behaviour changes. Separately named, distinct future candidate (not actioned): `FileScheduler`'s file-as-queue — an append-only job file polled with a 300 ms delay plus a sidecar position file — reimplements a durable queue/WAL; the idiomatic .NET answer for resumable local durable state is an embedded store (SQLite via `Microsoft.Data.Sqlite`), which deletes the poll loop and position file, the distributed case already covered by the satellited Redis / Azure Service Bus schedulers. See `docs/adr/0011-file-persistence-prep.md`. **Update:** that candidate is decided — adopted as the new **`WebReaper.Sqlite` satellite** (scheduler + visited-link tracker), *not* a core replacement: `Microsoft.Data.Sqlite` is a native-interop dependency (native `e_sqlite3` via SQLitePCLRaw), the exact class ADR-0009 quarantines off core, so the core `FileScheduler` poll loop + position file *stay* as the zero-dep default and SQLite is an opt-in robust-local tier ("the poll loop disappears" holds only for the opt-in consumer; the producer/consumer empty-wait remains, as `RedisScheduler` also polls). `SqliteVisitedLinkTracker` deliberately keeps no in-memory mirror — the table *is* the set, mirroring `RedisVisitedLinkTracker`, not the file adapter. The config blob stays on `FileBlobStore` (write-once/read-once, ADR-0003, no poll loop); a Sqlite config/blob store and a Sqlite sink are named, not-actioned future candidates. Purely additive SemVer (new satellite, no core change). See `docs/adr/0012-sqlite-embedded-store-satellite.md`.
- **The Crawl driver's retry was an `internal static` Polly pass-through with no seam; now `IRetryPolicy` with a hand-rolled core default — Polly leaves core. Additive, internal-only refactor.** `Infra.Executor` was a 14-line wrapper over `Polly.Policy.Handle<Exception>().RetryAsync(3)` with one call site, one fixed policy, and a dead synchronous `Retry<T>(Action)` overload (unused generic, zero callers). Three real adapter variations existed unnamed: the core default, a no-retry policy for deterministic tests, and consumer/satellite policies (Polly resilience pipelines, exponential backoff, navigation-aware retries). The `Handle<Exception>` catch-all also silently caught `OperationCanceledException`, so cancellation paid up to three wasted Spider invocations before propagating — a latent bug surfaced only when this seam was opened. Resolved: the **Retry policy** (`IRetryPolicy.ExecuteAsync<T>`) is a Tier-1 named seam; the core default `FixedAttemptsRetryPolicy` is hand-rolled (four attempts, no delay, cancellation cooperative by construction), so the `Polly` `PackageReference` leaves core (ADR-0009 dependency-light principle restored for this slice). Consumers wanting Polly's resilience pipeline wrap it in an `IRetryPolicy` adapter behind `ScraperEngineBuilder.WithRetryPolicy(...)`. Rejected with load-bearing reasons so future reviews don't re-suggest them: pure deletion (silently removes retries, doesn't fix the cancellation bug); a fix-in-place on `Executor` (leaves Polly + the static-helper shape + the hypothetical-seam problem); deepening into a broad *resilience* seam (one operation, one collaborator — narrow seam, not broad); pushing retry into `IPageLoader` (leaves parse/scheduler-edge failures uncovered); exposing `WithRetryPolicy` on `DistributedSpiderBuilder` (compounds with queue-level redelivery — the "double-retry storm" anti-pattern). See `docs/adr/0026-retry-policy-seam.md`.
- **The per-Job Spider shell leaked its result through side channels; termination was a thrown exception with no returned home — now a Job report value interpreted by a named Crawl driver. Deliberate, breaking.** `ISpider` was `CrawlAsync → List<Job>`; emitted data/callbacks escaped via `event`s on the concrete `Spider`, the crawl limit via a thrown `PageCrawlLimitException` run through the static `Executor` wrapper's `Polly.Handle<Exception>` fault-retry (retry-amplified; `Executor` itself was later retired in ADR-0026 in favour of the `IRetryPolicy` seam, see the previous bullet), dedup folded silently into the list — so no test ever constructed the real `Spider` and the live-site IntegrationTests were the only thing exercising orchestration. The Visited-link tracker was the root miscoupling: Crawl-global state wired as a per-Job collaborator, the shared cause of the limit exception, the racy discovery dedup (children filtered against the visited set but marked only when themselves crawled, under `Parallel.ForEachAsync`), and the distributed poison message (`WebReaper.AzureFuncs` had no driver to catch the throw → Service Bus dead-lettered the queue at the limit). Resolved: the **Crawl driver** is a named role with two adapters (in-process / distributed); the **Spider** is reduced to load → **Crawl step** → **Job report**; the **Visited-link tracker** moves to the driver as the single idempotency authority (atomic test-and-set gating dedup + limit + accounting); termination is the **Outstanding-work latch** (one seam, two adapters), `StopWhenDrained` subsumed. The page limit is now stated soft/best-effort and uniform across drivers (was accidental in-process, poison distributed). ADR 0001's closed `CrawlOutcome` is untouched — the **Job report** wraps it; the ADR-0001 closed-returned-value move re-applied one layer up. Rejected with load-bearing reasons so future reviews don't re-suggest them: a durable-workflow coordinator (β — vendor coupling + a second, unrelated completion mechanism), emergent queue-drain (a documented non-solution; "empty queue ≠ terminated" is a theorem, Kshemkalyani & Singhal Ch. 7), an exact distributed limit, and transport dedup-window as the sole correctness mechanism. See `docs/adr/0022-crawl-driver-and-outstanding-work-latch.md` and `research/distributed-crawl-termination.md`.
- **The core public surface was accidentally wide — public-because-a-test-or-satellite-binds-it, not public-because-it-is-contract — and its ~552 CS1591 was an open, unenforced backlog. Now the surface *is* the documented contract, drawn by the deletion test and enforced. Deliberate, breaking (9.0.0).** The split is not `*/Abstract`-public vs `*/Concrete`-internal: it is the **deletion test** per type — *named by a documented consumer / inherited by a satellite / part of the taught fluent API* ⇒ **Tier-1**, public, documented to the bar the codebase already set (`ScraperEngine`/`HttpPageLoadTransport` summaries); *reached only through a fluent builder method and named by nobody* ⇒ **Tier-2**, implementation, made `internal`. Tier-1 (Builders, every `*/Abstract` seam, the `Domain` model, `WebReaperJson`, `LoggerExtensions`, the payload-shell bases satellites inherit, the in-memory defaults the DIY-distributed pattern wires by hand, public exceptions) is documented; Tier-2 (the `File*`/`InMemory*` leaves, sinks/formats, parsers/loaders, `Spider`/`CrawlStep`/`InMemoryOutstandingWorkLatch`, `ValidatedProxyProvider`, the static `Executor` retry wrapper (since retired by ADR-0026 in favour of the `IRetryPolicy` seam — the default `FixedAttemptsRetryPolicy` is its successor and is also Tier-2), `ColorConsoleLogger`, the `Timer`/`Counter` log helpers) is `internal` — which clears ~half the backlog with **no filler doc and no NoWarn** (CS1591 does not fire on non-public members). `[InternalsVisibleTo]` targets the test + AOT-smoke assemblies only, never a shipped package (verified: no satellite-prod/Example/Misc names a Tier-2 type). The mechanical "remaining CS1591 == Tier-2" heuristic mis-classified four contract types the deletion test pulled back to Tier-1: `ScraperEngine` (the runtime object `BuildAsync` returns), `StaticProxySource`/`HttpProxyValidator` (the only built-in `WithValidatedProxies` inputs), and `SchemaFold<TNode>` (the ADR-0002 custom-backend reuse vehicle). Enforcement is project-wide `WarningsAsErrors=CS1591` — an undocumented public member now fails the build, so the backlog cannot re-accumulate (it accumulated *because* nothing failed). Rejected with load-bearing reasons: document-everything-to-zero (filler on types no one calls; destroys the signal), bulk `NoWarn` on core (reinstates the invisible backlog), a factory to keep the surface "concrete-free" (a shallow module — interface as complex as its one-line body), and a non-breaking 8.1.0/9.0.0 split. Core CS1591 went 294 → 0, contract-enforced. See `docs/adr/0023-core-doc-contract.md`.
- **The Schema grammar's rules — Field non-empty, leaf Selector non-empty, list-container Selector non-empty — lived in the fold (`SchemaFold`), so an invalid Schema constructed cleanly and failed only at parse time, asymmetrically (a leaf with no Selector got swallowed by the per-leaf catch and silently dropped the field; an object-list with no Selector aborted the whole parse). Now the rules live at the Schema construction site. Internal-only refactor with one narrowly-breaking edge.** `Schema.Add(SchemaElement)` enforces the grammar at the add call — the exact line a user wrote the bad element — throwing `ArgumentException` with a specific actionable message. `Schema.ListOf(field, selector, …children)` is the named factory for the object-list shape, bundling the `IsList + Selector + Children` triple a user previously had to remember together. The fold's existing `Field is null` guard and `RequireSelector` checks are kept as belt-and-suspenders for the one remaining path (mutation of a SchemaElement *after* Add — records here use `{ get; set; }`, not init-only) and annotated as invariant-assertions, not user-facing failure modes. The `ListSchemaWithoutSelectorThrows` test (whose body asserted the *silent* swallow-and-log, contradicting its own name) is updated to actually throw at construction, with a sibling pinning the same fast-fail for the object-list arm — the two arms are now uniform. The non-list nested Schema (an object container that uses the parent scope) is the one exemption, by design: the fold never reads its Selector. Rejected with load-bearing reasons so future reviews don't re-suggest them: replacing the collection-initialiser DSL with a fluent builder (regression in readability over Add-site validation); making SchemaElement properties `init`-only (closes mutation-after-Add but breaks any external procedural-build pattern — a follow-up candidate if the deeper cut is later wanted); splitting the model into leaf/container/root sibling types (major refactor for limited locality gain); internalising the `SchemaElement()` parameterless ctor (not necessary once Add validates Field non-empty — its remaining footprint is benign); documenting-rather-than-enforcing the rules (CLAUDE.md "Gotchas" can't fail-fast at the keystroke). See `docs/adr/0028-schema-construction-guards.md`. Companion: the ADR-0002 "missing-selector unified across single-value vs list paths" update is now structural at construction, not just at the fold.
- **The Schema fold's per-leaf swallow-and-log policy was emergent — a single `catch (Exception)` with a generic "Error during parsing phase" log line covered every failure mode, no test pinned it, and a coercion failure (`FormatException` from `int.Parse`) looked identical in the output and the log to a backend error or a malformed selector. Now the policy is the documented contract, with differentiated log messages and pinning tests. Internal-only refactor.** The fold's per-leaf `try/catch` ([SchemaFold:99-106](https://github.com/alex-on-ai/WebReaper/blob/master/WebReaper/Core/Parser/Concrete/SchemaFold.cs#L99-L106)) is split into three arms ordered most-specific first — `FormatException` ("Coercion to {Type} failed for field '{Field}' …"), `OverflowException` ("Coercion to {Type} overflowed …"), and the catch-all `Exception` ("Unexpected error extracting field '{Field}' (selector '{Selector}') …") — preserving the existing swallow-and-log breadth so behaviour at the contract surface is unchanged: same leaves stay unset, no extra exceptions propagate. A noisy page never aborting the crawl is load-bearing for a web scraper (a malformed number for one field must not lose every other field on the page); a strict-throw policy is the wrong default. The differentiation makes operations triaging "field missing" alerts distinguish *page had bad data* (Coercion log) from *selector is wrong* (Unexpected error log). New `TypedCoercionFailureTests` pin the policy across every `DataType` arm (Integer non-numeric / Integer overflow / Float / Boolean / DataTime) and the typed-leaf-list whole-list-drop property. Rejected with load-bearing reasons so future reviews don't re-suggest them: throwing on coercion failure (silently-noisy pages are the common case; aborting an entire page is wrong for the domain); a `SchemaErrorPolicy` knob (no consumer asked for strict mode; one adapter is a hypothetical seam, LANGUAGE.md); emitting a typed error marker into the output JSON (breaking shape change for every downstream sink); narrowing the catch to just `FormatException`/`OverflowException` and letting backend bugs propagate (a behaviour change at the contract surface — backend bugs ARE silently absorbed today, and that is desirable for crawl robustness without consumer feedback otherwise); per-element drop for typed leaf-lists with one malformed element (a behaviour change at the contract surface — distinct follow-up if demand emerges). See `docs/adr/0029-coercion-failure-policy.md`.
- **`LinkPathSelector`'s grammar — `Selector` non-empty, `PaginationSelector` non-empty-when-present, `PageActions` content only with `PageType.Dynamic` — lived nowhere on the construction surface; an invalid selector compiled cleanly and failed late at the Crawl step (empty `Selector`) or silently dropped its page actions (`PageActions` with a static transport that ignores them). Now the grammar is enforced at the `LinkPathSelector` construction site. Internal-only refactor with one narrowly-breaking edge.** The **selector chain** is the sibling declarative DSL to the **Schema** (ADR-0028) — equally load-bearing crawl state — but its element record had no construction-time rules: the fluent `ConfigBuilder` did its own `ThrowIfNullOrWhiteSpace` for its four `Follow`/`FollowWithBrowser`/`Paginate`/`PaginateWithBrowser` arguments, leaving the JSON codec (`SelectorChainJsonConverter.ReadSelector` calls `new LinkPathSelector` directly) and direct-`new` consumers (a DIY-distributed worker rematerialising Jobs from persisted state) unguarded, and catching the silent `PageActions`-with-`Static` feature-drop nowhere. Resolved: the `LinkPathSelector` primary constructor validates the three rules via property initializers (the idiomatic positional-record validation point — every construction path funnels through it); `LinkPathSelector.Follow` / `.Paginate` are named factories for the two intent-shapes (the sibling pair to `Schema.ListOf`); `ConfigBuilder`'s eight `ThrowIfNullOrWhiteSpace` calls collapse to zero (each method becomes a one-line factory delegation); the JSON codec gains a `JsonException` on a missing `selector` so a corrupt persisted Job fails at queue-read with the property name, not late at the crawl. `PageActions` follows ADR-0028's *empty equals absent* rule — an empty list is allowed with any `PageType`, only a non-empty list with `Static` is rejected. The constructor and the `Paginate` factory split on the pagination selector: the constructor allows a `null` `PaginationSelector` (that *is* the plain-follow shape, round-tripped by the JSON codec) so it can reject only an empty *non-null* one; the `Paginate` factory carries the paginate *intent* and additionally rejects a `null` pagination selector — a paginate step with no pagination selector is malformed (the existing `BuilderArgumentValidationTests`, pinning `Paginate(x, null)` ⇒ throw, caught this). Rejected with load-bearing reasons so future reviews don't re-suggest them: factory-only guards (leaves the codec + direct-`new` paths unguarded — `LinkPathSelector` has multiple construction sites, unlike Schema's single `Add`); rejecting any non-null `PageActions` (breaks the clear-the-list-but-don't-null refactor; contradicts the empty-equals-absent rule); allowing `Static` + non-empty `PageActions` (re-admits the silent feature-drop); merging `Follow`/`FollowWithBrowser` into one overloaded method (loses the satellite-dependency-in-the-method-name discoverability ADR-0009 made load-bearing); internalising the primary constructor (a follow-up if a deeper cut is wanted, same posture as ADR-0028's note on `SchemaElement`); two-layer enforcement keeping the `ConfigBuilder` guards (dead code — the constructor throws on the same inputs); documentation-not-enforcement (docs can't fail-fast at the keystroke). See `docs/adr/0030-link-path-selector-construction-guards.md`.
- **The URL-merge — folding the page URL into the emitted JSON record — was copy-pasted across four Sinks as the first line of `EmitAsync`, and the four ran concurrently against one shared `JsonObject`. Now `ParsedData`'s construction owns the merge and the Crawl driver clones per Sink. Behaviourally-additive.** Every persisting **Sink** needs the URL *inside* the record; `BufferedFileSink`, `CosmosSink`, `MongoDbSink`, `RedisSink` each opened `EmitAsync` with `entity.Data["url"] = entity.Url` — and `ConsoleSink` did not (the drifted copy: the URL silently absent from console output — the ADR-0002/0003/0006 "copies drifted into a one-copy bug" shape, in the non-file-sink cluster ADR-0006 did not reach). Worse, the **Crawl driver** fans every target page out under `Task.WhenAll` handing all Sinks the *same* `ParsedData`, so the four merges were concurrent structural mutations of one non-thread-safe `JsonObject` (`CosmosSink` additionally writes an `"id"`). Resolved: `ParsedData`'s construction folds the URL into `Data` (the property-initializer trick ADR-0030 used on `LinkPathSelector`) — one merge home, every `ParsedData` structurally carries `"url"`; the four Sinks drop their merge line; `ScraperEngine.ProcessTargetPage` deep-clones `Data` per Sink at the fan-out, so each Sink owns its `JsonObject` and may mutate freely. `ParsedData`'s public positional shape `(string Url, JsonObject Data)` and the `CrawlStep` construction call are unchanged; `Url` stays as the typed accessor. `"url"` is a documented reserved key — a Schema field named `url` is overwritten by the page URL. Rejected with load-bearing reasons so future reviews don't re-suggest them: merging in `CrawlStep` (leaves the invariant enforced by convention — a second construction site would skip it; the type should own it, the ADR-0028/0030 pattern); collapsing `ParsedData` to a bare `JsonObject` (loses the typed `Url` and the invariant's home; needlessly breaking a Tier-1 type); a shared per-Sink merge helper (still a per-Sink call every Sink must remember — a hypothetical seam); forbidding Sink mutation by contract (unenforceable — a custom Sink won't know); an immutable `Data` (forces every enriching Sink like Cosmos to clone by hand — N clones, not one); a computed `Url => Data["url"]` (marginally purer single-source but a JSON lookup per read; the stored set-once `Url` on an immutable record cannot drift). See `docs/adr/0031-parseddata-url-merge.md`.
- **The Crawl driver's stop rule was a concept the domain language named but no module realised — it was smeared across `ScraperEngine.RunAsync` as inline conditionals and re-hand-rolled, drifted, in the consumer-authored distributed driver. Now it is one module; the Outstanding-work latch's two-call credit protocol collapses to one atomic op. Deliberate, breaking.** Termination lived as three separate things in `RunAsync` — the **Outstanding-work latch** (each call guarded by `if (config.StopWhenDrained)`, so the seam ADR-0022 built as *the* completion mechanism was inert in a default crawl), the soft page limit (no seam, an inline `GetVisitedLinksCount() >= PageCrawlLimit` check written twice), and an empty-start-URLs short-circuit — with four `Scheduler.Complete()` sites; the distributed driver (`WebReaperSpider`) hand-rolled the latch protocol and **had no page-limit gate at all** (ADR-0022's "the distributed driver reads the limit gate" prose described a behaviour the copy never got). The latch's credit-conservation ordering (`AddAsync` before `SignalProcessedAsync`) lived only in XML-doc and `// Credit children BEFORE …` comments, re-hand-rolled in all three places — the ADR-0002/0003/0006 "copies drifted into a one-copy bug" shape, here in the termination cluster. Resolved: two concepts, **completion** (the latch — exact, consensus-requiring) and **cutoff** (the soft page limit — threshold-based, overshoot-tolerant), are composed by a new internal `StopRule` module, the one home for "should this Crawl stop, and why?"; the in-process `ScraperEngine` consults it (the four `Complete()` sites become two verdict-acts) instead of inlining the logic. `IOutstandingWorkLatch` loses `AddAsync`; `SignalProcessedAsync(childCount)` credits children and returns the parent's unit in one atomic step (one Redis round-trip, not INCRBY+DECRBY), so the latch *interface* carries zero ordering obligations — the driver still places that one call before it enqueues the children (children must be credited before they can be dequeued — intrinsic), but that is one latch call before one scheduler call, not a two-method protocol drifting across copies. Rejected with load-bearing reasons so future reviews don't re-suggest them: merging the limit into the latch (corrupts the latch's clean credit-to-zero invariant, or bolts on a second unrelated mechanism — the β-smell ADR-0022 rejected); a stop-rule module shared with the distributed driver (ADR-0009 makes that driver consumer-authored — a module a consumer must remember to call does not prevent drift; the latch is the shared primitive, and a primitive whose *use* is structurally required cannot drift); an `IStopRule` seam (one adapter is a hypothetical seam, LANGUAGE.md); building the cutoff *family* now (time/error/volume budgets — speculative, one cutoff is one adapter); making "stop" cease the driver's consumption instead of `Scheduler.Complete()` (a real improvement — it would fix `Complete()` being a no-op for durable schedulers — but it changes termination *semantics* and *behaviour*; deferred, not rejected, as a distinct follow-up). `IOutstandingWorkLatch` is a Tier-1 public seam: breaking, batched into 10.0.0. See `docs/adr/0032-stop-rule-module.md`.
- **Async adapter warm-up was a recurring lifecycle pattern with no named home — Stephen Cleary's asynchronous-initialization shape (fire the task in the constructor, expose a `Task Initialization` property) realised inconsistently across ten adapters. Now it is one opt-in capability the Crawl driver drives, and constructors do no async work. Deliberate, breaking.** `Initialization` was declared on `IScheduler` and `IVisitedLinkTracker` but not `IScraperSink`; ten durable adapters (`FileScheduler`, `RedisScheduler`, `SqliteScheduler`, `AzureServiceBusScheduler`, `FileVisitedLinkedTracker`, `RedisVisitedLinkTracker`, `SqliteVisitedLinkTracker`, `RedisSink`, `MongoDbSink`, `CosmosSink`) hand-rolled `Initialization = InitializeAsync()` in their constructor; the member's visibility had drifted (`CosmosSink.Initialization` `public`, `MongoDbSink`/`RedisSink` `private`); and *consumption* was realised three ways — the in-process **Crawl driver** awaiting scheduler + tracker once up front, `SqliteScheduler`/`SqliteVisitedLinkTracker` *also* self-guarding every method, the three sinks self-guarding every `EmitAsync` because the member was not on `IScraperSink` for the driver to await. The constructor-fired task is Cleary's pattern's acknowledged compromise — async work whose exceptions surface only on a later await. Resolved: a new opt-in `IAsyncInitializable` capability (`Task InitializeAsync()`, `WebReaper.Infra.Abstract`), orthogonal to the role interfaces — the `IAsyncDisposable` model, which `AzureServiceBusScheduler` already follows for async teardown; the in-process **Crawl driver** warms up every adapter it holds that has the capability (scheduler, tracker, every **Sink**), once, before the crawl loop (the `IHostedService` shape); constructors become pure and `InitializeAsync` is idempotent via `Lazy<Task>`; the ten self-guards are deleted; `IScheduler`/`IVisitedLinkTracker` lose `Initialization`, `IScraperSink` is untouched (warm-up was never a sink's role contract). Rejected with load-bearing reasons so future reviews don't re-suggest them: adding `Initialization` to `IScraperSink` (standardises the antipattern and still does not name warm-up — dominated); a role-interface base all three extend (no BCL precedent for welding a non-intrinsic lifecycle onto a role contract — `IEnumerator<T> : IDisposable` only because the cleanup *is* intrinsic; permanent `Task.CompletedTask` no-op boilerplate on every warm-up-less adapter); keeping Cleary's property form (names the seam but keeps async work in the constructor); `InitializeAsync(CancellationToken)` strict-call-once (a double-call re-runs destructive `DataCleanupOnStart` cleanup, and there is no single call-once site for the per-message distributed driver — idempotent + parameterless mirrors `IAsyncDisposable.DisposeAsync()`); async factory / `AsyncLazy<T>` returning the built adapter (the canonical async-construction pattern, but the synchronous fluent builder has no `await` point to host it); an abstract base class for the `Lazy<Task>` machinery (`Lazy<Task>` is already the BCL primitive). `IScheduler`/`IVisitedLinkTracker` are Tier-1 public seams: breaking, batched into 10.0.0. See `docs/adr/0033-async-warmup-seam.md`.
- **The per-Job Spider shell re-fetched the whole immutable `ScraperConfig` from `IScraperConfigStorage` on every Job to read two run-scoped fields; now it takes those two values at construction and holds no config storage. Deliberate, breaking.** `Spider.CrawlAsync` opened with `await ScraperConfigStorage.GetConfigAsync()` to read `Headless` (folded into the **PageRequest**) and `ParsingScheme` (passed to the **Crawl step**) — both immutable for the whole crawl, yet re-read per page; in distributed mode that `GetConfigAsync()` is a remote round-trip on every page. The dependency was a pass-through (the **deletion test**: drop it and the two values relocate to the one builder that wires the Spider, not across N callers), and the implicit "the shell fetches config at crawl time" contract was a footgun — the `WebReaper.AzureFuncs` worker built its spider without wiring config storage, so the first Job threw `ConfigNotFoundException`. Resolved: the `Spider` constructor takes `(ICrawlStep, IPageLoader, bool headless, Schema? parsingScheme)` — the two values, not the whole record (the narrow interface is the honest one, and it drops `SpiderTests`' `IScraperConfigStorage` double); `SpiderBuilder` gains `WithConfig(ScraperConfig)` and loses its config-storage methods; `DistributedSpiderBuilder.BuildSpider()` becomes `BuildSpider(ScraperConfig config)` — a required terminal parameter, so a worker spider cannot be built without a config (ADR-0025 "misbuild is unrepresentable"). `IScraperConfigStorage` stays a seam — owned by the **Crawl driver** (the in-process engine persists and reads it; the distributed worker fetches it and passes the object), never by the shell, as `ICrawlStep`'s own "no config storage behind this seam" doc already implied. `ParsingScheme` is *not* pushed into `CrawlStep` — `ICrawlStep` is pure and deterministic in `(job, document, schema)` (ADR-0002), so the schema is a parameter by design, not step state. Rejected with load-bearing reasons so future reviews don't re-suggest them: the shell taking the whole `ScraperConfig` (a wide interface for a shell that reads 2 of 9 fields); memoizing the first `GetConfigAsync()` (treats the repeat-I/O symptom, keeps the wrong dependency and the footgun); an async `BuildSpiderAsync()` keeping `WithConfigStorage` (sync→async is breaking anyway, and it re-hides the fetch inside the builder); splitting `ScraperConfig` into driver-config and shell-config records (a real two-audiences observation, but the record is serialized — out of scope, a far larger change). `DistributedSpiderBuilder` is a Tier-1 public seam: breaking, batched into 10.0.0. See `docs/adr/0034-spider-config-at-construction.md`.
- **`PageAction` was a `(PageActionType, object[])` pair — a six-arm enum, an untyped parameter array, and a four-entry Puppeteer dispatch dictionary: three lists the compiler never cross-checked. Now it is a closed sum of typed arms. Deliberate, breaking.** What `Parameters` had to hold for a given `Type` lived, unenforced, in three places — the builder, the serialization codec (~75 lines of per-value kind-tagging, which its own doc said existed only because the `object[]` is "genuinely polymorphic"), and the transport's `Convert.ToInt32` / `(string)` casts; the dictionary handled four of the six arms, so `WaitForSelector` and `EvaluateExpression` — both reachable from the public `PageActionBuilder` — threw a bare `KeyNotFoundException` mid-crawl. The *shape* was the defect; wiring two dictionary entries (parked twice as "a missing feature" — ADR-0004, the 2026-05-20 plan) treats the symptom. Resolved: `PageAction` is an `abstract record` with six nested sealed-record arms carrying typed fields (`PageAction.Click(string Selector)`, `PageAction.WaitForSelector(string Selector, int TimeoutMs)`, …) — the ADR-0001 `CrawlOutcome` closed-sum pattern; `PageActionType` is removed (the arm is the discriminant); the codec becomes a `type` discriminator + the arm's typed fields, the kind-tagging deleted; the transport dispatches with a `switch` over the arms with an actionable default, and the two unwired actions are wired (the closed sum makes the gap un-ignorable). `PageActionBuilder`'s public method signatures are unchanged — only direct `new PageAction(PageActionType.X, …)` / `PageActionType` switches break. Rejected with load-bearing reasons so future reviews don't re-suggest them: wiring the two missing dictionary entries and keeping the enum + `object[]` (treats the symptom — the codec, the casts and the parallel-list shape all remain); a `Match` / visitor for hard compile-time exhaustiveness (the closed sum already removes the parallel enum + dictionary — the candidate's defect; a six-delegate `Match` is ceremony beyond what `CrawlOutcome` carries, and the dispatch must live in the satellite anyway — ADR-0009 — so a future un-handled arm is an actionable typed throw, not a silent miss); STJ native polymorphism attributes (departs from ADR-0008's hand-written-converter grammar — `Schema` / `SchemaElement` / the selector chain are all hand-written — for no gain, since the converter simplifies regardless); deleting the two unwired arms (they are advertised public `PageActionBuilder` methods — a worse break than wiring two one-line PuppeteerSharp calls). `PageAction` / `PageActionType` are Tier-1 public `Domain` types: breaking, batched into 10.0.0. See `docs/adr/0035-pageaction-closed-sum.md`.
- **`ILinkParser` was a one-adapter seam — a public interface with a single internal adapter, one caller, and no builder method to supply another; link extraction was never a seam. Now it is a concrete static function. Deliberate, breaking.** `ILinkParser` had exactly one implementation (`LinkParserByCssSelector`, ~8 lines of AngleSharp), one caller (`CrawlStep`), and — alone among `SpiderBuilder`'s runtime collaborators — no `WithX` registration method: a public Tier-1 interface (ADR-0023) a consumer could see but not supply. ADR-0002 had already *named* it "a single-adapter seam … indirection without variation", but only answered "do not fold it into `ISchemaBackend`" — not "should it be an interface at all". The deletion test (LANGUAGE.md) settles it: delete the interface and complexity does not reappear across callers — it is pass-through ceremony. Resolved: `ILinkParser` is removed; link extraction is `LinkExtractor`, an `internal static` function `CrawlStep` calls directly. Link discovery stays a concern of its own — a separate file from the Schema fold, ADR-0002 preserved — but a function, not a seam. A latent crash is fixed in passing: an `<a>` matched by the selector but carrying no usable `href` (absent, empty, or whitespace) reached `new Uri(baseUrl, null)` and threw `ArgumentNullException` mid-crawl; such elements are now skipped. Rejected with load-bearing reasons so future reviews don't re-suggest them: adding `WithLinkParser` to complete the seam (a hypothetical seam with a permanent public knob — speculative generality; no consumer ever supplied one, and ADR-0009's registration seam never listed it); fixing only the crash and keeping the interface (punts the shape — the public-but-unsuppliable interface remains); inlining extraction into `CrawlStep` (merges link discovery into the crawl-step decision — ADR-0002 keeps them separate concerns); keeping the interface for a future XPath/JSONPath link extractor (the `GetLinksAsync(Uri, string, string)` signature is HTML/CSS-shaped — JSON link-following rides the Schema fold as content fields, not a parallel adapter; a real second shape extracts the interface then, from two implementations). `ILinkParser` is a Tier-1 public seam: breaking, batched into 10.0.0. See `docs/adr/0036-link-extraction-not-a-seam.md`.
- **`IScheduler.Complete()` was the in-process Crawl driver's one lever for ending a Crawl — a default-no-op interface member only the in-memory scheduler honoured, so a durable scheduler ran forever. Now the driver ends a Crawl by ceasing its own consumption of the job stream, and `Complete()` is removed. Deliberate, breaking.** The driver drives `Parallel.ForEachAsync` over `Scheduler.GetAllAsync()`; the loop ends only when that stream ends, and the only mechanism was `Scheduler.Complete()` — which ships as `void Complete() { }` and is overridden by `InMemoryScheduler` alone. `FileScheduler` (a **Core adapter**, the zero-dependency durable default), `RedisScheduler`, `SqliteScheduler` and `AzureServiceBusScheduler` inherited the no-op, so with any of them `RunAsync` never returned: the loop body saw `stopRule.IsCrawlOver` and `return`ed, but `GetAllAsync()` kept yielding forever — a silent hang the moment a durable scheduler was wired. Termination was modelled as a producer-side signal ("tell the scheduler to stop producing"), but a durable, possibly-shared scheduler genuinely cannot know another worker won't enqueue a Job — `Complete()` was structurally a no-op for it, not laziness. The reframe: every `GetAllAsync(ct)` adapter already honours its `CancellationToken` (the in-memory channel's `ReadAllAsync(ct)`, the durable adapters' `while (!ct.IsCancellationRequested)` poll loops), so there was already a universal stop mechanism and `Complete()` was a redundant, partial second one. Resolved: `IScheduler.Complete()` is removed; the in-process **Crawl driver** owns a `CancellationTokenSource` linked to the caller's token, drives `GetAllAsync` and `Parallel.ForEachAsync` on it, and cancels it when the **Stop rule** concludes — `RunAsync` catches the resulting `OperationCanceledException` (a driver self-cancel, caller token still live) as the normal end of a finished Crawl and returns. Termination is now uniform across every scheduler; in-flight Jobs at a cutoff (the soft page limit) abort rather than finish — within the limit's documented overshoot tolerance, and tighter to the cap. This closes ADR-0032's deferred option (d). The `OperationCanceledException` does not contradict ADR-0022's "termination is a value": the **Stop rule**'s verdict is still a `bool`; the OCE is the cooperative-cancellation unwind of the driver's own `Parallel.ForEachAsync`, caught in the same method, never crossing a seam, never retry-amplified (the **Retry policy** excludes OCE — ADR-0026). Rejected with load-bearing reasons so future reviews don't re-suggest them: keeping `Complete()` but making it a mandatory non-default member (a durable shared scheduler cannot honour "no more Jobs ever" — the member would be a forced lie, and it keeps two mechanisms where the token alone is universal); documenting the limitation harder (a documented footgun is still a silent hang); a driver-side wrapper enumerable that `yield break`s on the verdict without cancellation (deadlocks the completion case — the wrapper is parked inside the underlying `MoveNextAsync` and never regains control to check the verdict); preserving "in-flight finishes" on a cutoff via two separate tokens (fiddlier, leans on `Parallel.ForEachAsync` source-fault behaviour; the soft limit tolerates the overshoot); a wrapper that swallows the conclude-OCE so `RunAsync` never sees it (C# forbids `yield` inside `try`/`catch` — more machinery than one catch at the driver boundary). `IScheduler` is a Tier-1 public seam: breaking, batched into 10.0.0. See `docs/adr/0037-stop-ceases-consumption.md`.
- **The post-extraction surface was three accreted mechanisms — `PostProcess` (`Func<Metadata,JsonObject,Task>`, single-assignment), `Subscribe` (`Action<ParsedData>`, multicast), and the **Sink** — for what are two roles. Now it is two seams: a **Page processor** pipeline and the **Sink**. Deliberate, breaking.** The **Crawl driver** fanned every **Target page** to three differently-shaped places, two of them anonymous delegate constructor parameters named nowhere in CONTEXT.md. `Subscribe`/`ScrapedData`'s capability — observe a `ParsedData` — is a strict subset of a **Sink**'s (which adds async, cancellation, **Adapter warm-up**, a parallel-safe clone): a shallow seam shadowing the deep one, and it leaked ADR-0031 — it was handed the *shared* record, not a clone. `PostProcess` earns its keep — the only hook seeing the raw page + ancestry — but was mis-shaped: anonymous, single-assignment (a second registration silently dropped the first), and its `Metadata` doc promised an "enrich or filter" the `Func<…,Task>` return could not deliver. Resolved: two roles — Emit (the **Sink**, terminal concurrent fan-out, unchanged) and Process (a new `IPageProcessor` ordered pipeline the driver runs before the fan-out, seeing the record + raw HTML + ancestry + `Schema?`, returning a closed two-arm `PageVerdict` — `Kept` | `Dropped`). `Subscribe` folds into the **Sink** seam as `DelegateSink` sugar (signature unchanged, so call sites compile; the shallow parallel seam gone, the clone-leak closed); `PostProcess` / `PostProcessor` / `Metadata` are removed. The interface was designed three ways in parallel (minimal / flexible / common-caller) and the shipped shape is a hybrid: one-method `IPageProcessor`, a two-arm closed `PageVerdict`, `ValueTask`, the pipeline threads `ParsedData` (not a bare `JsonObject`), three `Process` registration overloads, a processor throw drops the page. Rejected with load-bearing reasons so future reviews don't re-suggest them: one seam — everything a **Sink** (`PostProcess` needs the raw HTML, which would bloat what flows to every database/file **Sink**, and the concurrency models differ — ordered pipeline vs concurrent fan-out); keeping three seams and only documenting them (naming `Subscribe` does not make a shallow shadow deep); a third `ShortCircuit` verdict arm (skipping a downstream processor is *that* processor's own check, not an earlier one controlling the pipeline); an ASP.NET-style `next` middleware parameter (the "forgot to call `next`, the pipeline silently truncates" footgun — composition is a processor holding a processor, no seam feature); a bare-`JsonObject` pipeline (re-introduces a post-pipeline `"url"` re-merge ADR-0031 consolidated into `ParsedData`'s constructor); a 7-method registration surface (trades one shallow seam for a wide shallow builder — three `Process` overloads suffice); a processor throw entering the per-Job retry policy (the pipeline runs outside the `IRetryPolicy`-wrapped Spider call, and a flaky LLM should not re-crawl the page — a throw drops the page, the ADR-0029 posture). The page-processor seam is the home the AI work (validation, confidence-scoring, selector-repair, classification) lands on. `ScraperEngineBuilder.PostProcess` and the `Metadata` Tier-1 `Domain` type are removed: breaking, batched into 10.0.0. See `docs/adr/0038-page-processor-seam.md`.
- **The content-extraction seam was named `IJsonContentParser` for its output and fronted by three shallow `*ContentParser` shells — the seam read as a JSON-*input* parser, and the shells disguised one fold as three adapters. Now it is `IContentExtractor`, shells collapsed. Deliberate, breaking.** `IJsonContentParser`'s "Json" named the `JsonObject` *output* (ADR-0008) but read as the JSON *input* format and collided with the class `JsonContentParser` (the JSON-input shell); the qualifier was a fossil — it once distinguished the System.Text.Json parser from the Newtonsoft `IContentParser` removed at 6.0.0, a sibling long gone. `AngleSharpContentParser` / `XPathContentParser` / `JsonContentParser` were `internal` pass-through shells, each `new`ing `SchemaFold<TNode>` with one backend and delegating the single method — making the **Schema fold** (one adapter) look like three; ADR-0002 created them as a documented source-compat concession, and ADR-0023 (making `*/Concrete` internal) silently removed the surface that concession protected. Resolved: `IJsonContentParser` → `IContentExtractor`, `ParseToJsonAsync` → `ExtractAsync`, `SchemaContentParser<TNode>` → `SchemaFold<TNode>`, `WithContentParser` → `WithContentExtractor`; the three shells are deleted and the builder constructs `new SchemaFold<TNode>(backend, logger)` directly — the extension idiom ADR-0002 already documents and `SchemaFoldTests` already uses. The interface doc's claim that a `null` schema yields an empty object — contradicted by the fold's own `ArgumentNullException.ThrowIfNull` — is corrected to match the throw. The seam *stays* a seam, unlike `ILinkParser` (ADR-0036): `WithContentExtractor` is an existing, public, used registration method, and the extraction-*strategy* axis genuinely varies — the deterministic fold today, an LLM-backed extractor next, fitting `ExtractAsync(string, Schema?)` exactly. Rejected with load-bearing reasons so future reviews don't re-suggest them: keeping the shells (pure pass-through ceremony — the deletion test, LANGUAGE.md); collapsing the seam too, `ILinkParser`-style (its second adapter is imminent and fits the signature — a seam waits for its second adapter, and this one is at the door); renaming `WithJsonContentParser` / `WithXPathContentParser` (they honestly select a backend — "parse as JSON" is true — and are referenced by issue #27 / discussion #17 and every example); renaming the `Core/Parser` namespace (blast radius out of proportion to the gain); adding a `CancellationToken` to `ExtractAsync` now (deferred to the AI-features work — shape the interface from the second adapter, not for it). `IJsonContentParser` / `SchemaContentParser<TNode>` / `WithContentParser` are Tier-1 public surface: breaking, batched into 10.0.0. See `docs/adr/0039-content-extractor-seam.md`.
- **`PageAction.Fill` and `PageAction.ScrollIntoView` carry an implicit 30 s timeout policy, not an explicit `TimeoutMs` record field. Discipline call, recorded so a future arm-author can apply it.** ADR-0074 adds three form-interaction arms (`Fill(Selector, Value)`, `Press(Key)`, `ScrollIntoView(Selector)`); `Fill` and `ScrollIntoView` both auto-wait for their selector before dispatching (CDP reuses the ADR-0057 `WaitForSelectorAsync` helper; Playwright auto-waits natively), capped at 30 s. The timeout could have been an explicit field (the same shape `WaitForSelector` carries); rejected because the 30 s default would be written verbatim on nearly every call site, bloating the record + codec + tool descriptor for a value no caller varies. The rule: **a record-level timeout that is the safety net for the common case stays implicit; a load-bearing parameter that varies per call (the `WaitForSelector` shape) stays explicit.** Composition handles the rare custom-timeout case: `WaitForSelector(sel, custom_timeout) + Fill(sel, value)`, the outer arm shadowing the inner safety net. The v1 implicit-timeout set is closed at these two arms; a future arm-author proposing a third should re-read this bullet and the [ADR-0074 §Considered options (i)](docs/adr/0074-pageaction-form-interaction-primitives.md) reasoning before extending. See `docs/adr/0074-pageaction-form-interaction-primitives.md`.
