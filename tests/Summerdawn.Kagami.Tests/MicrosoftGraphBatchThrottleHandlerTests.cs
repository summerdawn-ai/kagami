using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.DependencyInjection;

namespace Summerdawn.Kagami.Tests;

/// <summary>
/// Verifies that <see cref="MicrosoftGraphBatchThrottleHandler"/> correctly promotes throttled
/// batch sub-responses to a retryable outer 429 with an accurate <c>Retry-After</c> header.
/// </summary>
public sealed class MicrosoftGraphBatchThrottleHandlerTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Builds a handler pipeline: ThrottleHandler → fakeInner.</summary>
    private static HttpClient BuildClient(HttpResponseMessage fakeResponse)
    {
        var inner = new StaticResponseHandler(fakeResponse);
        var throttle = new MicrosoftGraphBatchThrottleHandler { InnerHandler = inner };
        return new HttpClient(throttle);
    }

    private static HttpRequestMessage BatchRequest() =>
        new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/$batch");

    private static HttpResponseMessage OkBatchResponse(string batchJson) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(batchJson, Encoding.UTF8, "application/json"),
        };

    private static string BatchJson(params JsonObject[] subResponses)
    {
        var array = new JsonArray();
        foreach (var r in subResponses)
        {
            array.Add(r);
        }
        return new JsonObject { ["responses"] = array }.ToJsonString();
    }

    private static JsonObject SubResponse(int status, string id = "1", int? retryAfterSeconds = null)
    {
        var obj = new JsonObject { ["id"] = id, ["status"] = status };
        if (retryAfterSeconds is { } s)
        {
            obj["headers"] = new JsonObject { ["Retry-After"] = s.ToString() };
        }
        return obj;
    }

    // ── pass-through cases ────────────────────────────────────────────────

    [Fact]
    public async Task NonBatchRequest_IsPassedThroughUnmodified()
    {
        var inner = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var throttle = new MicrosoftGraphBatchThrottleHandler { InnerHandler = inner };
        var client = new HttpClient(throttle);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task AllSuccessfulSubResponses_ArePassedThroughUnmodified()
    {
        string json = BatchJson(SubResponse(200, "1"), SubResponse(200, "2"));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    // ── throttle promotion ────────────────────────────────────────────────

    [Fact]
    public async Task ThrottledSubResponse_PromotesOuterStatusTo429()
    {
        string json = BatchJson(SubResponse(429, "1", retryAfterSeconds: 6));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task ThrottledSubResponse_SetsReasonPhrase()
    {
        string json = BatchJson(SubResponse(429, "1", retryAfterSeconds: 6));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());

        Assert.Equal("Too Many Requests", response.ReasonPhrase);
    }

    [Fact]
    public async Task ThrottledSubResponse_SetsRetryAfterHeader()
    {
        string json = BatchJson(SubResponse(429, "1", retryAfterSeconds: 6));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());

        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal("6", values.Single());
    }

    [Fact]
    public async Task MultipleThrottledSubResponses_UsesLargestRetryAfter()
    {
        string json = BatchJson(
            SubResponse(429, "1", retryAfterSeconds: 6),
            SubResponse(429, "2", retryAfterSeconds: 30));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());

        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal("30", values.Single());
    }

    [Fact]
    public async Task ThrottledSubResponse_BodyRemainsReadable()
    {
        string json = BatchJson(SubResponse(429, "1", retryAfterSeconds: 6));
        var client = BuildClient(OkBatchResponse(json));

        var response = await client.SendAsync(BatchRequest());
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"responses\"", body);
    }

    // ── inner handler ──────────────────────────────────────────────────────

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
