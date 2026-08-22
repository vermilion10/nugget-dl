using System.Net;

namespace NuggetDl.Core.Sources;

/// <summary>
/// Retries GET requests that come back with a 5xx status, with exponential
/// backoff.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private const int MaxRetries = 3;
    private const double BackoffFactorSeconds = 0.5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // 0.5 * (2 ^ (attempt - 1)) seconds: 0.5s, 1s, 2s
                var delaySeconds = BackoffFactorSeconds * Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }

            response?.Dispose();

            // HttpRequestMessage cannot be sent twice, so each retry after the
            // first needs its own clone of the original request.
            using var attemptRequest = attempt == 0 ? request : await CloneAsync(request);
            response = await base.SendAsync(attemptRequest, cancellationToken);

            var shouldRetry = request.Method == HttpMethod.Get
                && RetryableStatusCodes.Contains(response.StatusCode)
                && attempt < MaxRetries;

            if (!shouldRetry)
            {
                return response;
            }
        }

        return response!;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var buffer = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(buffer);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
