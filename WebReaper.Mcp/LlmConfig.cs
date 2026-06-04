namespace WebReaper.Mcp;

// ADR-0086 / ADR-0084: the LLM config for extract_with_prompt. The API key is
// environment-only (never a tool parameter); the model may be overridden per
// call. Resolution + validation is pure (a closed truth table under test); the
// env reads happen at the call site.
public sealed record LlmConfig(string Model, string BaseUrl, string? ApiKey)
{
    public const string ModelEnvVar = "WEBREAPER_LLM_MODEL";
    public const string BaseUrlEnvVar = "WEBREAPER_LLM_BASE_URL";

    /// <summary>
    /// Resolve the effective config: a per-call model override wins over the
    /// environment model. Throws an actionable <see cref="InvalidOperationException"/>
    /// when the model or the base URL is missing.
    /// </summary>
    public static LlmConfig Resolve(string? modelOverride, string? envModel, string? baseUrl, string? apiKey)
    {
        var model = FirstNonBlank(modelOverride, envModel);
        if (model is null)
            throw new InvalidOperationException(
                $"extract_with_prompt needs an LLM model: pass the 'model' parameter or set {ModelEnvVar}.");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"extract_with_prompt needs an LLM endpoint: set {BaseUrlEnvVar} (e.g. https://api.openai.com/v1).");

        return new LlmConfig(model, baseUrl.Trim(), string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
