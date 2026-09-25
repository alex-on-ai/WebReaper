using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WebReaper.AI.Http;
using Xunit;

namespace WebReaper.AI.Http.Tests;

public class OpenAiCompatibleChatClientTests
{
    private const string CannedResponse =
        """{"model":"gpt-4o-mini","choices":[{"message":{"content":"{\"title\":\"hi\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}""";

    // Issue #265: LM Studio's 400 body for response_format json_object.
    private const string LmStudioJsonObjectRejection =
        """{"error":"'response_format.type' must be 'json_schema' or 'text'"}""";

    private static (OpenAiCompatibleChatClient Client, CapturingHandler Handler) NewClient(
        string? apiKey = "sk-test",
        string responseJson = CannedResponse,
        HttpStatusCode status = HttpStatusCode.OK)
        => NewClient(new CapturingHandler((status, responseJson)), apiKey);

    private static (OpenAiCompatibleChatClient Client, CapturingHandler Handler) NewClient(
        CapturingHandler handler, string? apiKey = "sk-test")
    {
        var client = new OpenAiCompatibleChatClient(
            "https://api.example.com/v1", "gpt-4o-mini", apiKey, new HttpClient(handler));
        return (client, handler);
    }

    [Fact]
    public async Task Posts_to_chat_completions_with_model_and_messages()
    {
        var (client, handler) = NewClient();
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "hello") };

        await client.GetResponseAsync(messages);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.Request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        var msgs = root.GetProperty("messages");
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        Assert.Equal("sys", msgs[0].GetProperty("content").GetString());
        Assert.Equal("user", msgs[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Maps_temperature_max_tokens_and_json_response_format()
    {
        var (client, handler) = NewClient();
        var options = new ChatOptions
        {
            Temperature = 0.2f,
            MaxOutputTokens = 256,
            ResponseFormat = ChatResponseFormat.Json,
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options);

        Assert.Single(handler.RequestBodies);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.True(Math.Abs(root.GetProperty("temperature").GetDouble() - 0.2) < 1e-6);
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Omits_response_format_when_not_json()
    {
        var (client, handler) = NewClient();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], new ChatOptions());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task Parses_content_and_usage()
    {
        var (client, _) = NewClient();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal("""{"title":"hi"}""", response.Text);
        Assert.Equal(11, response.Usage!.InputTokenCount);
        Assert.Equal(7, response.Usage.OutputTokenCount);
        Assert.Equal(18, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task Sends_bearer_when_api_key_present()
    {
        var (client, handler) = NewClient(apiKey: "sk-abc");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-abc", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Omits_authorization_when_api_key_absent_or_blank()
    {
        var (client, handler) = NewClient(apiKey: "   ");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Throws_on_non_success_status_carrying_the_code()
    {
        var (client, _) = NewClient(responseJson: "no", status: HttpStatusCode.Unauthorized);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Retries_without_response_format_when_the_server_rejects_json_object()
    {
        var (client, handler) = NewClient(new CapturingHandler(
            (HttpStatusCode.BadRequest, LmStudioJsonObjectRejection),
            (HttpStatusCode.OK, CannedResponse)));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "x")], new ChatOptions { ResponseFormat = ChatResponseFormat.Json });

        Assert.Equal("""{"title":"hi"}""", response.Text);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal("json_object", ResponseFormatType(handler.RequestBodies[0]));
        Assert.Null(ResponseFormatType(handler.RequestBodies[1]));
    }

    [Fact]
    public async Task Omits_json_object_on_later_calls_once_the_retry_succeeded()
    {
        var (client, handler) = NewClient(new CapturingHandler(
            (HttpStatusCode.BadRequest, LmStudioJsonObjectRejection),
            (HttpStatusCode.OK, CannedResponse)));
        var json = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], json);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")], json);

        // One rejected request, one retry, then a single request up front.
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Null(ResponseFormatType(handler.RequestBodies[2]));
    }

    [Fact]
    public async Task Does_not_retry_a_400_that_does_not_name_response_format()
    {
        var (client, handler) = NewClient(
            responseJson: """{"error":"context length exceeded"}""", status: HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "x")], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }));

        Assert.Contains("400", ex.Message);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task Surfaces_the_retry_failure_and_keeps_json_object_when_the_retry_fails()
    {
        var (client, handler) = NewClient(new CapturingHandler(
            (HttpStatusCode.BadRequest, LmStudioJsonObjectRejection),
            (HttpStatusCode.NotFound, """{"error":"model not found"}"""),
            (HttpStatusCode.OK, CannedResponse)));
        var json = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], json));
        Assert.Contains("404", ex.Message);
        Assert.Contains("model not found", ex.Message);

        // The retry did not succeed, so the next call still asks for json_object.
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")], json);
        Assert.Equal("json_object", ResponseFormatType(handler.RequestBodies[2]));
    }

    [Fact]
    public async Task Throws_NotSupported_when_tools_are_present()
    {
        var (client, _) = NewClient();
        var options = new ChatOptions { Tools = [new DummyTool()] };
        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options));
    }

    [Fact]
    public void GetService_returns_self_for_assignable_types_only()
    {
        var (client, _) = NewClient();
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(client, client.GetService(typeof(OpenAiCompatibleChatClient)));
        Assert.Null(client.GetService(typeof(string)));
    }

    private static string? ResponseFormatType(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.TryGetProperty("response_format", out var format)
            ? format.GetProperty("type").GetString()
            : null;
    }

    // Answers with the replies in order, repeating the last one, and records
    // every request body.
    private sealed class CapturingHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _calls;

        public HttpRequestMessage? Request { get; private set; }
        public List<string> RequestBodies { get; } = [];
        public string? RequestBody => RequestBodies.LastOrDefault();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = replies[Math.Min(_calls++, replies.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class DummyTool : AIFunction
    {
        public override string Name => "dummy";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}
