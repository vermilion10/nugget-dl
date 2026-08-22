using System.Net;

namespace NuggetDl.Core.Sources;

/// <summary>
/// Provides a shared, pooled <see cref="HttpClient"/> for the process.
/// Connection pooling and retries are configured once via <see cref="RetryHandler"/>
/// and a socket handler with a bounded connection pool, instead of per-request setup.
/// </summary>
public static class CdnHttpClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<HttpClient> LazyClient = new(CreateClient);

    public static HttpClient Instance => LazyClient.Value;

    private static HttpClient CreateClient()
    {
        var socketHandler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = ConnectTimeout,
            // The CDNs gzip-compress JSON/tab responses without always
            // setting a Content-Length that matches, and requests.Session
            // (Python) transparently decompresses these - HttpClient needs
            // this set explicitly or Utf8JsonReader chokes on the raw
            // gzip magic bytes (0x1F 0x8B) instead of '{'.
            AutomaticDecompression = DecompressionMethods.All,
        };

        var retryHandler = new RetryHandler
        {
            InnerHandler = socketHandler,
        };

        return new HttpClient(retryHandler)
        {
            Timeout = RequestTimeout,
        };
    }
}
