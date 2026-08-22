using System.IO.Compression;

namespace NuggetDl.Core.Sources;

/// <summary>
/// A source backed by an Android OBB expansion file (a zip archive).
/// </summary>
public sealed class ObbSource : ISource
{
    private readonly string _obbPath;
    private readonly string _filename;
    private readonly IBundleIndexReader _bundleIndexReader;

    private readonly Dictionary<string, BundleIndexEntry> _index;
    private readonly Dictionary<string, string> _resources;

    public ObbSource(string obb, IBundleIndexReader bundleIndexReader)
    {
        _obbPath = obb;
        _bundleIndexReader = bundleIndexReader;
        _filename = obb;

        using var zip = ZipFile.OpenRead(obb);
        _index = LoadIndex(zip, bundleIndexReader);
        _resources = BuildResourceMap(zip.Entries.Select(e => e.FullName));
    }

    public Task<bool> HasBlobAsync(string blob, CancellationToken ct = default) =>
        Task.FromResult(_resources.ContainsKey(blob));

    public Task<byte[]> GetBlobAsync(string blob, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(_obbPath);
        var entry = zip.GetEntry(_resources[blob])
            ?? throw new BlobNotFoundException($"Entry {_resources[blob]} not found in {_obbPath}");

        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return Task.FromResult(memory.ToArray());
    }

    public Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default) =>
        Task.FromResult(_index.TryGetValue(bundle, out var entry) ? entry.Blob : null);

    public Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default) =>
        Task.FromResult(_index.TryGetValue(bundle, out var entry) ? entry.Sha1 : null);

    public Task<Version?> SourceVersionAsync(CancellationToken ct = default) =>
        Task.FromResult<Version?>(null);

    public Task<IReadOnlyDictionary<string, string>> ResourcesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(_resources);

    public Task<IReadOnlyCollection<string>> BundleNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_index.Keys);

    public override string ToString() => $"ObbSource({_filename})";

    private static Dictionary<string, BundleIndexEntry> LoadIndex(
        ZipArchive zip, IBundleIndexReader bundleIndexReader)
    {
        var indexEntry = zip.GetEntry("assets/resource/matrix/index")
            ?? throw new SourceIndexException("assets/resource/matrix/index not found in OBB");

        using var entryStream = indexEntry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        var indexBlob = memory.ToArray();

        var indexBytes = bundleIndexReader.ReadTextAsset(indexBlob, ["assets/buildtemp/index.bytes"])
            ?? throw new SourceIndexException("Invalid OBB index bundle");

        return IndexCodec.ParseIndex(indexBytes);
    }

    private static Dictionary<string, string> BuildResourceMap(IEnumerable<string> filenames)
    {
        var resources = new Dictionary<string, string>();
        var names = filenames.ToList();

        foreach (var name in names)
        {
            if (name.Contains("matrix"))
            {
                resources[name.Split('/')[^1]] = name;
            }
        }

        var resourcesAssets = names.FirstOrDefault(n => n.Split('/')[^1] == "resources.assets");
        if (resourcesAssets is not null)
        {
            resources["resources.assets"] = resourcesAssets;
        }

        return resources;
    }
}
