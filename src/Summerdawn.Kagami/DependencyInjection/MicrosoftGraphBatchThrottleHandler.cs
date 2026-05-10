using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Summerdawn.Kagami.DependencyInjection;

/// <summary>
/// Converts Microsoft Graph <c>$batch</c> responses with throttled sub-requests into a retryable outer response.
/// </summary>
internal sealed class MicrosoftGraphBatchThrottleHandler : DelegatingHandler
{
    /// <summary>
    /// Sends the request and inspects Microsoft Graph batch responses for throttled sub-requests.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (request.RequestUri is null
            || request.Method != HttpMethod.Post
            || !request.RequestUri.AbsolutePath.EndsWith("/$batch", StringComparison.OrdinalIgnoreCase)
            || !response.IsSuccessStatusCode)
        {
            return response;
        }

        // Read the batch body once so we can inspect sub-responses and then restore it for downstream readers.
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("responses", out var responses))
        {
            return response;
        }

        TimeSpan? retryAfter = null;
        foreach (var subResponse in responses.EnumerateArray())
        {
            if (!subResponse.TryGetProperty("status", out var status) || status.GetInt32() != (int)HttpStatusCode.TooManyRequests)
            {
                continue;
            }

            retryAfter = Max(retryAfter, ReadRetryAfter(subResponse));
            response.StatusCode = HttpStatusCode.TooManyRequests;
        }

        if (retryAfter is not null)
        {
            // Surface the longest retry interval from the throttled sub-responses.
            response.Headers.Remove("Retry-After");
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Restore the response body so the connector can parse the batch payload normally.
        response.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(contentType));
        return response;
    }

    /// <summary>
    /// Reads the <c>Retry-After</c> header from a batch sub-response.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(JsonElement response)
    {
        if (!response.TryGetProperty("headers", out var headers)
            || headers.ValueKind != JsonValueKind.Object
            || !headers.TryGetProperty("Retry-After", out var retryAfter))
        {
            return null;
        }

        string? value = retryAfter.GetString();
        if (int.TryParse(value, out int seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// Returns the larger of two retry delays.
    /// </summary>
    private static TimeSpan? Max(TimeSpan? left, TimeSpan? right) =>
        left is null || (right is not null && right > left) ? right : left;
}
