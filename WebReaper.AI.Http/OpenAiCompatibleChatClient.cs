using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace WebReaper.AI.Http;

/// <summary>
/// An AOT-clean <see cref="IChatClient"/> that speaks the OpenAI Chat
/// Completions protocol over a raw <see cref="HttpClient"/> (ADR-0084).
/// No provider SDK: the request/response shapes go through System.Text.Json
/// source generation (<see cref="OpenAiJsonContext"/>), so the client
/// composes into a Native-AOT binary such as the WebReaper CLI.
/// <para>
/// Point the base URL at any OpenAI-compatible
/// <c>/chat/completions</c> endpoint: OpenAI (<c>https://api.openai.com/v1</c>),
/// Ollama (<c>http://localhost:11434/v1</c>), OpenRouter, vLLM, LM Studio, or
/// an Anthropic-compatible gateway. Hand the instance to WebReaper's
/// <c>WithLlmExtractor</c> / <c>WithLlmSchemaInferrer</c>.
/// </para>
/// <para>
/// Scope (ADR-0084 piece 2): JSON-mode chat completions, which is all the
/// <c>--prompt</c> / <c>--infer</c> extraction paths need. Tool calling
/// (the agent / action-resolver path) throws <see cref="NotSupportedException"/>
/// rather than silently dropping the tools.
/// </para>
/// <para>
/// JSON mode sends OpenAI's <c>response_format</c> <c>json_object</c>. A
/// server that rejects it with a 400 naming <c>response_format</c> (LM Studio
/// accepts only <c>json_schema</c> and <c>text</c>) gets the request once more
/// without it, leaving the JSON instruction to the prompt. Once that retry
/// succeeds, later calls on the instance omit <c>response_format</c> up front.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;
    private readonly string _defaultModel;
    private readonly string? _apiKey;

    // Set once the endpoint rejected json_object and the retry without it
    // succeeded: a server capability, so later calls skip the rejected round
    // trip. Concurrent calls may each pay it once before this is visible.
    private volatile bool _jsonObjectRejected;

    /// <summary>Construct the client.</summary>
    /// <param name="baseUrl">The endpoint base, e.g.
    /// <c>https://api.openai.com/v1</c> or <c>http://localhost:11434/v1</c>.
    /// <c>/chat/completions</c> is appended.</param>
    /// <param name="model">The default model id, used when a per-call
    /// <see cref="ChatOptions.ModelId"/> is not supplied.</param>
    /// <param name="apiKey">Optional bearer token. Omitted (e.g. local
    /// Ollama) when null or empty, so no <c>Authorization</c> header is sent.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse.
    /// When null, the client owns a private instance and disposes it.</param>
    public OpenAiCompatibleChatClient(
        string baseUrl,
        string model,
        string? apiKey = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        _defaultModel = model;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (options?.Tools is { Count: > 0 })
        {
            throw new NotSupportedException(
                "OpenAiCompatibleChatClient does not support tool calling (ADR-0084 " +
                "piece 2 ships JSON-mode chat completions for --prompt / --infer). " +
                "Use a tool-calling-capable IChatClient for the agent or action resolver.");
        }

        var request = new ChatCompletionRequest
        {
            Model = options?.ModelId ?? _defaultModel,
            Messages = BuildMessages(messages),
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxOutputTokens,
            ResponseFormat = options?.ResponseFormat is ChatResponseFormatJson && !_jsonObjectRejected
                ? new ResponseFormatSpec { Type = "json_object" }
                : null,
        };

        using var httpResponse = await PostAsync(request, cancellationToken).ConfigureAwait(false);

        ChatCompletionResponse? completion;
        await using (var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            completion = await JsonSerializer
                .DeserializeAsync(stream, OpenAiJsonContext.Default.ChatCompletionResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        if (completion?.Choices is not { Count: > 0 } choices || choices[0].Message is null)
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible endpoint {_endpoint} returned no choices.");
        }

        var content = choices[0].Message!.Content ?? string.Empty;
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = completion.Model ?? request.Model,
        };

        if (completion.Usage is { } usage)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage.PromptTokens,
                OutputTokenCount = usage.CompletionTokens,
                TotalTokenCount = usage.TotalTokens,
            };
        }

        return response;
    }

    /// <summary>Not implemented: WebReaper's LLM adapters use the
    /// non-streaming <see cref="GetResponseAsync"/>. Throws
    /// <see cref="NotSupportedException"/>.</summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "OpenAiCompatibleChatClient does not implement streaming; use GetResponseAsync.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private static List<ChatCompletionRequestMessage> BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatCompletionRequestMessage>();
        foreach (var message in messages)
        {
            result.Add(new ChatCompletionRequestMessage
            {
                // ChatRole.Value is the canonical "system" / "user" /
                // "assistant" / "tool" string the protocol expects.
                Role = message.Role.Value,
                Content = message.Text ?? string.Empty,
            });
        }

        return result;
    }

    // POST the request and return the successful response, or throw
    // HttpRequestException carrying the status and body. Issue #265: a 400
    // naming response_format means the server refused json_object itself (LM
    // Studio answers {"error":"'response_format.type' must be 'json_schema'
    // or 'text'"}), so send the request once more without it. The prompt
    // still asks for JSON, and WebReaper's LlmCall strips code fences and
    // re-asks once on a parse failure.
    private async Task<HttpResponseMessage> PostAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
            if (request.ResponseFormat is null || !RejectsResponseFormat(response.StatusCode, body))
            {
                throw EndpointError(response, body);
            }
        }

        request.ResponseFormat = null;
        var retry = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!retry.IsSuccessStatusCode)
        {
            using (retry)
            {
                var body = await ReadBodySafeAsync(retry, cancellationToken).ConfigureAwait(false);
                throw EndpointError(retry, body);
            }
        }

        _jsonObjectRejected = true;
        return retry;
    }

    private async Task<HttpResponseMessage> SendAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        var payload = JsonSerializer.Serialize(request, OpenAiJsonContext.Default.ChatCompletionRequest);
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (_apiKey is not null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool RejectsResponseFormat(HttpStatusCode status, string body) =>
        status == HttpStatusCode.BadRequest
        && body.Contains("response_format", StringComparison.Ordinal);

    private HttpRequestException EndpointError(HttpResponseMessage response, string body) =>
        new($"OpenAI-compatible endpoint {_endpoint} returned " +
            $"{(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 600)}");

    private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
