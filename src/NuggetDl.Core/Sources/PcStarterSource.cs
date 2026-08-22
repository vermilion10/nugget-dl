using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuggetDl.Core.Sources;

/// <summary>
/// A source backed by the game's PC launcher CDN.
public sealed class PcStarterSource : ISource
{
    private readonly string _cdnName;
    private readonly PcStarterData _cdn;
    private readonly string _section;
    private readonly IBundleIndexReader _bundleIndexReader;

    private JsonNode? _cdnIndex;
    private Dictionary<string, BundleIndexEntry>? _matrixIndex;
    private Dictionary<string, string>? _resources;

    public PcStarterSource(PcStarterData cdn, string cdnName, bool prerelease, IBundleIndexReader bundleIndexReader)
    {
        _cdn = cdn;
        _cdnName = cdnName;
        _section = prerelease ? "predownload" : "default";
        _bundleIndexReader = bundleIndexReader;
    }

    public async Task<bool> HasBlobAsync(string blob, CancellationToken ct = default) =>
        (await ResourcesAsync(ct)).ContainsKey(blob);

    public async Task<byte[]> GetBlobAsync(string blob, CancellationToken ct = default)
    {
        var resources = await ResourcesAsync(ct);
        var url = resources[blob];
        var response = await CdnHttpClient.Instance.GetAsync(url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BlobDownloadException($"Failed to download blob {blob} - {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default)
    {
        var index = await MatrixIndexAsync(ct);
        return index.TryGetValue(bundle, out var entry) ? entry.Blob : null;
    }

    public async Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default)
    {
        var index = await MatrixIndexAsync(ct);
        return index.TryGetValue(bundle, out var entry) ? entry.Sha1 : null;
    }

    public async Task<Version?> SourceVersionAsync(CancellationToken ct = default)
    {
        var cdnIndex = await CdnIndexAsync(ct);
        var version = cdnIndex![_section]!["version"]!.GetValue<string>();
        return Versions.Parse(version);
    }

    public async Task<IReadOnlyDictionary<string, string>> ResourcesAsync(CancellationToken ct = default)
    {
        if (_resources is not null)
        {
            return _resources;
        }

        var blobBase = await BlobCdnUrlAsync(ct);
        var cdnIndex = await CdnIndexAsync(ct);
        var resourcesUrl = blobBase + cdnIndex![_section]!["resources"]!.GetValue<string>();
        var resourceIndex = await GetJsonAsync(resourcesUrl, ct);

        var basePath = await BasePathAsync(ct);
        var resources = new Dictionary<string, string>();
        foreach (var resource in resourceIndex!["resource"]!.AsArray())
        {
            var dest = resource!["dest"]!.GetValue<string>();
            if (dest.StartsWith('/'))
            {
                dest = dest[1..];
            }

            var blob = dest.Split('/')[^1];
            resources[blob] = blobBase + basePath + dest;
        }

        _resources = resources;
        return resources;
    }

    public async Task<IReadOnlyCollection<string>> BundleNamesAsync(CancellationToken ct = default) =>
        (await MatrixIndexAsync(ct)).Keys;

    public override string ToString() => $"PcStarterSource({_cdnName})";

    private async Task<JsonNode?> CdnIndexAsync(CancellationToken ct)
    {
        if (_cdnIndex is not null)
        {
            return _cdnIndex;
        }

        _cdnIndex = await GetJsonAsync(_cdn.IndexUrl(), ct);
        return _cdnIndex;
    }

    private async Task<Dictionary<string, BundleIndexEntry>> MatrixIndexAsync(CancellationToken ct)
    {
        if (_matrixIndex is not null)
        {
            return _matrixIndex;
        }

        var resources = await ResourcesAsync(ct);
        var url = resources["index"];
        var response = await CdnHttpClient.Instance.GetAsync(url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BlobDownloadException($"Failed to download blob index - {(int)response.StatusCode}");
        }

        var indexBundle = await response.Content.ReadAsByteArrayAsync(ct);
        var indexBytes = _bundleIndexReader.ReadTextAsset(indexBundle, ["assets/temp/index.bytes"])
            ?? throw new SourceIndexException("Failed to find index in patch index bundle");

        _matrixIndex = IndexCodec.ParseIndex(indexBytes);
        return _matrixIndex;
    }

    private async Task<string> BasePathAsync(CancellationToken ct)
    {
        var cdnIndex = await CdnIndexAsync(ct);
        var url = cdnIndex![_section]!["resourcesBasePath"]!.GetValue<string>();
        return url.EndsWith('/') ? url : url + "/";
    }

    private async Task<string> BlobCdnUrlAsync(CancellationToken ct)
    {
        var cdnIndex = await CdnIndexAsync(ct);
        var url = cdnIndex!["default"]!["cdnList"]![0]!["url"]!.GetValue<string>();
        return url.EndsWith('/') ? url : url + "/";
    }

    private static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        var response = await CdnHttpClient.Instance.GetAsync(url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BlobDownloadException($"Failed to download json {url} - {(int)response.StatusCode}");
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }
}
