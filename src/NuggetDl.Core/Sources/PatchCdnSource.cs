using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NuggetDl.Core.Sources;

/// <summary>
/// A source backed by the game's patch CDN.
///
/// Construction is async (it fetches config.tab and possibly probes for the
/// document version), so use <see cref="CreateAsync"/> instead of a public
/// constructor.
/// </summary>
public sealed class PatchCdnSource : ISource
{
    // Highest patch component tried when scanning for the real document version.
    private const int DocumentVersionScanLimit = 25;

    private readonly string _cdnName;
    private readonly PatchCdnData _cdn;
    private readonly string _cdnUrl;
    private readonly string _version;
    private readonly string? _signKey;
    private readonly IBundleIndexReader _bundleIndexReader;

    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private Dictionary<string, BundleIndexEntry>? _index;
    private Dictionary<string, string>? _resources;

    private PatchCdnSource(
        string cdnName,
        PatchCdnData cdn,
        string cdnUrl,
        string version,
        string? signKey,
        IBundleIndexReader bundleIndexReader)
    {
        _cdnName = cdnName;
        _cdn = cdn;
        _cdnUrl = cdnUrl;
        _version = version;
        _signKey = signKey;
        _bundleIndexReader = bundleIndexReader;
    }

    public static async Task<PatchCdnSource> CreateAsync(
        string cdnName,
        PatchCdnData cdn,
        string version,
        IBundleIndexReader bundleIndexReader,
        string? key = null,
        CancellationToken ct = default)
    {
        if (key is not null)
        {
            cdn = cdn.WithKey(key);
        }

        string? signKey = null;
        if (cdn.Sign)
        {
            signKey = Environment.GetEnvironmentVariable("PATCH_SIGN_KEY");
            if (string.IsNullOrEmpty(signKey))
            {
                Console.Error.WriteLine("Beta selected but no PATCH_SIGN_KEY set");
            }
        }

        var config = await GetTabAsync(cdn.ConfigUrl(version), ct);
        var applicationVersion = version;
        var documentVersion = config["DocumentVersion"];

        // Temporary instance (cdnUrl not yet known) to reuse the signing/request
        // helpers while scanning for the real document version.
        var scanning = new PatchCdnSource(cdnName, cdn, cdnUrl: string.Empty, applicationVersion, signKey, bundleIndexReader);

        if (documentVersion == config["ApplicationVersion"])
        {
            documentVersion = await scanning.ScanDocumentVersionAsync(applicationVersion, ct);
        }

        var cdnUrl = cdn.BaseUrl(applicationVersion, documentVersion);
        return new PatchCdnSource(cdnName, cdn, cdnUrl, applicationVersion, signKey, bundleIndexReader);
    }

    public async Task<bool> HasBlobAsync(string blob, CancellationToken ct = default) =>
        (await ResourcesAsync(ct)).ContainsKey(blob);

    public async Task<byte[]> GetBlobAsync(string blob, CancellationToken ct = default)
    {
        var resources = await ResourcesAsync(ct);
        var url = resources[blob];
        var response = await RequestAsync(url, head: false, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BlobDownloadException($"Failed to download blob {blob} - {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default)
    {
        var index = await EnsureIndexAsync(ct);
        return index.TryGetValue(bundle, out var entry) ? entry.Blob : null;
    }

    public async Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default)
    {
        var index = await EnsureIndexAsync(ct);
        return index.TryGetValue(bundle, out var entry) ? entry.Sha1 : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResourcesAsync(CancellationToken ct = default)
    {
        if (_resources is not null)
        {
            return _resources;
        }

        var index = await EnsureIndexAsync(ct);
        var resources = new Dictionary<string, string>();
        foreach (var entry in index.Values)
        {
            resources[entry.Blob] = _cdnUrl + entry.Blob;
        }

        _resources = resources;
        return resources;
    }

    public Task<Version?> SourceVersionAsync(CancellationToken ct = default) =>
        Task.FromResult<Version?>(Versions.Parse(_version));

    public async Task<IReadOnlyCollection<string>> BundleNamesAsync(CancellationToken ct = default) =>
        (await EnsureIndexAsync(ct)).Keys;

    public override string ToString() => $"PatchCdnSource({_cdnName})";

    /// <summary>
    /// Fetches and caches the index on first use. Safe to call concurrently:
    /// only the first caller does the network round trip, others await the
    /// same result.
    /// </summary>
    private async Task<Dictionary<string, BundleIndexEntry>> EnsureIndexAsync(CancellationToken ct)
    {
        if (_index is not null)
        {
            return _index;
        }

        await _indexLock.WaitAsync(ct);
        try
        {
            if (_index is not null)
            {
                return _index;
            }

            var response = await RequestAsync($"{_cdnUrl}index", head: false, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new BlobDownloadException($"Failed to download patch index - {(int)response.StatusCode}");
            }

            var bundleBytes = await response.Content.ReadAsByteArrayAsync(ct);

            var indexBytes = _bundleIndexReader.ReadTextAsset(
                bundleBytes,
                ["assets/temp/index.bytes", "assets/buildtemp/index.bytes"]);

            if (indexBytes is null)
            {
                throw new SourceIndexException("Failed to find index in patch index bundle");
            }

            _index = IndexCodec.ParseIndex(indexBytes);
            return _index;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<string> ScanDocumentVersionAsync(string applicationVersion, CancellationToken ct)
    {
        var version = Versions.Parse(applicationVersion);
        var candidates = Enumerable.Range(0, DocumentVersionScanLimit + 1)
            .Select(patch => $"{version.Major}.{version.Minor}.{patch}")
            .ToList();

        var checks = candidates.Select(async candidate =>
        {
            var url = _cdn.BaseUrl(applicationVersion, candidate) + "index";
            var response = await RequestAsync(url, head: true, ct);
            return (candidate, ok: response.StatusCode == HttpStatusCode.OK);
        });

        var results = await Task.WhenAll(checks);
        var hits = results.Where(r => r.ok).Select(r => r.candidate).ToList();

        if (hits.Count > 0)
        {
            var chosen = hits[^1];
            Console.Error.WriteLine($"config.tab has a placeholder DocumentVersion, using {chosen}");
            return chosen;
        }

        throw new SourceIndexException(
            $"config.tab for {applicationVersion} has a placeholder DocumentVersion and no "
            + $"index was found for {version.Major}.{version.Minor}.0 through "
            + $"{version.Major}.{version.Minor}.{DocumentVersionScanLimit}");
    }

    private Task<HttpResponseMessage> RequestAsync(string url, bool head, CancellationToken ct)
    {
        var method = head ? HttpMethod.Head : HttpMethod.Get;

        if (!_cdn.Sign)
        {
            return CdnHttpClient.Instance.SendAsync(new HttpRequestMessage(method, url), ct);
        }

        // CN beta signature scheme.
        var expiration = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var randomStr = RandomAlphanumeric(10);
        var signaturePrefix = $"{expiration}-{randomStr}-0-";
        var path = new Uri(url).AbsolutePath;
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes($"{path}-{signaturePrefix}{_signKey}"))).ToLowerInvariant();
        var signature = signaturePrefix + hash;

        var signedUrl = url + "?sign=" + signature;
        return CdnHttpClient.Instance.SendAsync(new HttpRequestMessage(method, signedUrl), ct);
    }

    private static string RandomAlphanumeric(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    private static async Task<Dictionary<string, string>> GetTabAsync(string url, CancellationToken ct)
    {
        var response = await CdnHttpClient.Instance.GetAsync(url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BlobDownloadException($"Failed to download raw {url} - {(int)response.StatusCode}");
        }

        var content = await response.Content.ReadAsByteArrayAsync(ct);
        var text = Encoding.UTF8.GetString(content);

        var data = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length != 3)
            {
                throw new SourceIndexException($"Malformed config.tab line: '{trimmed}'");
            }

            data[parts[0]] = parts[2];
        }

        return data;
    }
}
