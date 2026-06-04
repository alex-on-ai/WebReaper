using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using WebReaper.AI.Llm;
using WebReaper.Domain.Agent;
using WebReaper.Domain.PageActions;
using WebReaper.Domain.Parsing;

namespace WebReaper.AI.Tools;

/// <summary>
/// The closed-sum-as-tools registries (ADR-0060, ADR-0060 amendment
/// 2026-05-28, ADR-0078 Axis B). Holds the per-arm tool projections for the
/// three <see cref="AgentDecision"/> arms whose tool shape is NOT shared with
/// <see cref="PageAction"/> (<see cref="Extract"/>, <see cref="Follow"/>,
/// <see cref="Stop"/>), and exposes the brain's and resolver's tool registries
/// (<see cref="ForBrain"/>, <see cref="ForResolver"/>) plus the parse that
/// decodes each registry's tool calls (<see cref="ParseBrainTool"/>,
/// <see cref="ParseResolverTool"/>).
/// <para>
/// Hand-rolled JSON Schema per arm (fork 4 — AOT-friendly, no reflection-built
/// schemas). Each arm's tool concerns (Name + Descriptor + FromArguments) live
/// in one nested static class: the three <see cref="AgentDecision"/>-native
/// arms here, the ten <see cref="PageAction"/> arms in
/// <see cref="PageActionTools"/>.
/// </para>
/// <para>
/// Derived registries (ADR-0078 Axis B): both registries and both parse paths
/// are views over one source list — the three native arms here plus
/// <see cref="PageActionTools.Arms"/>. Because a registry's offered tool and
/// the parse that decodes its call come from the same list, the registration
/// and the parse dispatch cannot drift (the pre-derivation hazard: register an
/// arm in <c>ForBrain</c> but forget its parse case, and the model's call
/// silently became <c>Stop</c>).
/// </para>
/// <para>
/// Brain registry: 13 tools — <see cref="Extract"/>, <see cref="Follow"/>,
/// <see cref="Stop"/>, plus the ten flat <c>Act*</c> arms (every
/// <see cref="PageAction"/> arm, including
/// <see cref="PageAction.SemanticAct"/>). The flat packaging (fork 2) keeps
/// every arm's schema simple; the model picks one; the SDK validates the args
/// against the per-arm schema.
/// </para>
/// <para>
/// Resolver registry: 9 tools — the nine concrete <see cref="PageAction"/>
/// arms, ever. No <see cref="PageAction.SemanticAct"/> on the resolver (fork
/// 8): the arm exposes no resolver adapter
/// (<see cref="PageActionArm.ResolverToAction"/> is <c>null</c>), so it is
/// structurally absent from a registry derived from the arm list — the model
/// literally cannot emit a <c>SemanticAct</c>-loop arm.
/// </para>
/// </summary>
internal static class AgentDecisionTools
{
    // ---- Per-arm tool projections (AgentDecision-specific arms) -------------

    /// <summary>Tool projection of <see cref="AgentDecision.Extract"/>.</summary>
    public static class Extract
    {
        public const string Name = "Extract";

        public static AIFunction Descriptor { get; } = new HandRolledAIFunction(
            name: Name,
            description:
                "Extract a record from the current page using a flat field-to-CSS-selector " +
                "schema. Use when the current page contains the records you want to capture. " +
                "Single-level only; no nested objects in v1.",
            parametersSchema: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Map of output field name to CSS selector. Single level — no nested objects.",
                        ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                    },
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Why this is the right next step.",
                    },
                },
                ["required"] = new JsonArray { (JsonNode)"schema", (JsonNode)"reason" },
            });

        /// <summary>Construct the arm from a tool call's argument JSON plus
        /// the audit-trail reason. Two failure modes:
        /// <c>"missing 'schema'"</c> when the schema property is absent or
        /// non-object, and <c>"schema was empty"</c> when the schema parses
        /// but yields no usable field/selector pairs (the v1 single-level
        /// flat shape rejects nested objects — they round-trip to an empty
        /// Schema).</summary>
        public static ToolCallResult<AgentDecision.Extract> FromArguments(JsonElement args, string reason)
        {
            if (!args.TryGetProperty("schema", out var schemaEl) ||
                schemaEl.ValueKind != JsonValueKind.Object)
            {
                return ToolCallResult<AgentDecision.Extract>.Failed("missing 'schema'");
            }
            var schema = BuildFlatSchema(schemaEl);
            if (schema.Children.Count == 0)
            {
                return ToolCallResult<AgentDecision.Extract>.Failed("schema was empty");
            }
            return ToolCallResult<AgentDecision.Extract>.Ok(
                new AgentDecision.Extract(schema) { Reason = reason });
        }

        // v1: single-level flat schema { "field": "selector", ... }. Nested
        // schemas (objects-within-objects, lists-of-objects) are a v2
        // deferral matching ADR-0045's source-gen v1 constraint. Kept
        // arm-local — the brain's Extract is the only consumer; the
        // schema-inferrer adapter has its own (more lenient) flattener
        // that handles two LLM-emitted shapes.
        private static Schema BuildFlatSchema(JsonElement schemaEl)
        {
            var s = new Schema();
            foreach (var prop in schemaEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var fieldName = prop.Name;
                var selector = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(selector)) continue;
                s.Add(new SchemaElement(fieldName, selector));
            }
            return s;
        }
    }

    /// <summary>Tool projection of <see cref="AgentDecision.Follow"/>.</summary>
    public static class Follow
    {
        public const string Name = "Follow";

        public static AIFunction Descriptor { get; } = new HandRolledAIFunction(
            name: Name,
            description:
                "Load a candidate URL as the next agent step. Pick the URL from the listed " +
                "candidate URLs on the current page. Do not propose a URL you have already " +
                "visited (the engine will reject it).",
            parametersSchema: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute URL chosen from the candidate list.",
                    },
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Why this URL is the right next step.",
                    },
                },
                ["required"] = new JsonArray { (JsonNode)"url", (JsonNode)"reason" },
            });

        public static ToolCallResult<AgentDecision.Follow> FromArguments(JsonElement args, string reason)
        {
            var url = LlmToolArguments.TryGetString(args, "url");
            return string.IsNullOrWhiteSpace(url)
                ? ToolCallResult<AgentDecision.Follow>.Failed("missing 'url'")
                : ToolCallResult<AgentDecision.Follow>.Ok(
                    new AgentDecision.Follow(url) { Reason = reason });
        }
    }

    /// <summary>Tool projection of <see cref="AgentDecision.Stop"/>.</summary>
    public static class Stop
    {
        public const string Name = "Stop";

        public static AIFunction Descriptor { get; } = new HandRolledAIFunction(
            name: Name,
            description:
                "Terminate the agent run. Use when the goal is satisfied OR the page set has " +
                "been exhausted without further progress.",
            parametersSchema: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Why the run is stopping — goal satisfied, dead-end, etc.",
                    },
                },
                ["required"] = new JsonArray { (JsonNode)"reason" },
            });

        /// <summary>Always succeeds — Stop has no required arguments beyond
        /// the audit-trail <c>Reason</c>. The <paramref name="args"/>
        /// JsonElement is ignored.</summary>
        public static ToolCallResult<AgentDecision.Stop> FromArguments(JsonElement args, string reason) =>
            ToolCallResult<AgentDecision.Stop>.Ok(new AgentDecision.Stop { Reason = reason });
    }

    // ---- Brain-native arms ---------------------------------------------------

    // The three AgentDecision arms whose tool shape is not a PageAction. Each
    // entry pairs the arm's descriptor with the parse that decodes its call to
    // an AgentDecision (Reason already populated from the audit-trail string).
    private sealed record NativeArm(
        AIFunction Descriptor,
        Func<JsonElement, string, AgentDecision> Parse);

    private static IReadOnlyList<NativeArm> NativeArms { get; } =
    [
        new(Extract.Descriptor, (args, reason) => Unwrap(Extract.FromArguments(args, reason), Extract.Name, reason)),
        new(Follow.Descriptor,  (args, reason) => Unwrap(Follow.FromArguments(args, reason),  Follow.Name,  reason)),
        new(Stop.Descriptor,    (args, reason) => Unwrap(Stop.FromArguments(args, reason),    Stop.Name,    reason)),
    ];

    // ---- Derived registries (ADR-0078 Axis B) --------------------------------

    /// <summary>The brain's 13-tool registry: a derived view over the three
    /// brain-native arms plus <see cref="PageActionTools.Arms"/> (the ten
    /// <see cref="PageAction"/> arms, flat-packed). Built from the same list as
    /// <see cref="ParseBrainTool"/>, so the offered tool and the parse that
    /// decodes it cannot drift.</summary>
    public static IReadOnlyList<AIFunction> ForBrain() =>
        [.. NativeArms.Select(a => a.Descriptor), .. PageActionTools.Arms.Select(a => a.Descriptor)];

    /// <summary>The resolver's 9-tool registry: <see cref="PageActionTools.Arms"/>
    /// restricted to the entries that expose a resolver adapter. No
    /// <see cref="PageAction.SemanticAct"/> (fork 8) — it has no resolver
    /// adapter, so it is structurally absent here and from
    /// <see cref="ParseResolverTool"/>, which derive from the same predicate.</summary>
    public static IReadOnlyList<AIFunction> ForResolver() =>
        [.. PageActionTools.Arms.Where(a => a.ResolverToAction is not null).Select(a => a.Descriptor)];

    // ---- Derived parse dispatch (ADR-0078 Axis B) ----------------------------

    // name -> brain parse, derived from the same list as ForBrain. Native arms
    // decode to their AgentDecision directly; PageAction arms decode to a
    // PageAction wrapped in Act. Keyed by the descriptor name the model emits.
    private static readonly Dictionary<string, Func<JsonElement, string, AgentDecision>> BrainParsers =
        BuildBrainParsers();

    // name -> resolver parse, derived from the SAME predicate as ForResolver
    // (entries with a resolver adapter), so offered <-> parseable. SemanticAct,
    // having no resolver adapter, is in neither.
    private static readonly Dictionary<string, Func<JsonElement, PageAction?>> ResolverParsers =
        PageActionTools.Arms
            .Where(a => a.ResolverToAction is not null)
            .ToDictionary(
                a => a.Descriptor.Name,
                a => (Func<JsonElement, PageAction?>)(args => a.ResolverToAction!(args).Value),
                StringComparer.Ordinal);

    private static Dictionary<string, Func<JsonElement, string, AgentDecision>> BuildBrainParsers()
    {
        var map = new Dictionary<string, Func<JsonElement, string, AgentDecision>>(StringComparer.Ordinal);
        foreach (var native in NativeArms)
            map[native.Descriptor.Name] = native.Parse;
        foreach (var arm in PageActionTools.Arms)
            map[arm.Descriptor.Name] = (args, reason) => UnwrapAct(arm.ToAction(args), arm.Descriptor.Name, reason);
        return map;
    }

    /// <summary>Decode a brain tool call into its <see cref="AgentDecision"/>
    /// arm (ADR-0060 amendment, ADR-0078 Axis B). The audit-trail <c>reason</c>
    /// is read once and threaded into every arm; a <see cref="PageAction"/> arm
    /// is wrapped in <see cref="AgentDecision.Act"/>; a per-arm
    /// <c>FromArguments</c> failure becomes <see cref="AgentDecision.Stop"/>
    /// with the failure reason composed into the audit string. An unregistered
    /// tool name (a genuine model hallucination — never a wiring omission now
    /// that the registry and this dispatch derive from one list) becomes
    /// <c>Stop</c>.</summary>
    public static AgentDecision ParseBrainTool(string toolName, JsonElement args)
    {
        var reason = LlmToolArguments.TryGetString(args, "reason") ?? "";
        return BrainParsers.TryGetValue(toolName, out var parse)
            ? parse(args, reason)
            : new AgentDecision.Stop { Reason = $"brain called unregistered tool '{toolName}'" };
    }

    /// <summary>Decode a resolver tool call into a concrete
    /// <see cref="PageAction"/> arm (ADR-0060, ADR-0078 Axis B), or <c>null</c>
    /// when the model invented a tool name or called the brain-only
    /// <c>ActSemanticAct</c> (which the resolver never registers — fork 8). A
    /// per-arm <c>FromArguments</c> failure also reads as <c>null</c>; the
    /// resolver's contract is "concrete arm, or nothing".</summary>
    public static PageAction? ParseResolverTool(string toolName, JsonElement args)
        => ResolverParsers.TryGetValue(toolName, out var parse) ? parse(args) : null;

    // Brain wrap for AgentDecision arms: the factory returns the arm with its
    // Reason already populated (the brain passed reason in); on failure the
    // freeform FailureReason composes into a Stop matching the pre-amendment
    // audit-trail format byte-for-byte.
    private static AgentDecision Unwrap<T>(ToolCallResult<T> result, string toolName, string reason)
        where T : AgentDecision =>
        result.Value is { } arm
            ? arm
            : new AgentDecision.Stop { Reason = $"brain {toolName} {result.FailureReason}: {reason}" };

    // Brain wrap for Act* arms: the factory returns the PageAction value; the
    // brain wraps it in Act with the audit-trail Reason. Failure -> Stop with
    // the factory's FailureReason in the same format as the pre-amendment parser.
    private static AgentDecision UnwrapAct(ToolCallResult<PageAction> result, string toolName, string reason) =>
        result.Value is { } action
            ? new AgentDecision.Act(action) { Reason = reason }
            : new AgentDecision.Stop { Reason = $"brain {toolName} {result.FailureReason}: {reason}" };
}
