namespace NuggetDl.Core.Sources;

/// <summary>
/// Where the interesting parts of an install live.
/// MatrixDirs is in precedence order, lowest first, so a later directory
/// overrides an earlier one on a bundle-name collision.
/// </summary>
public sealed record GameDirLayout(IReadOnlyList<string> MatrixDirs, string ResourcesAssetsPath);

/// <summary>Resolves an install root to its layout.</summary>
public static class GameDirResolver
{
    private static readonly (string StreamingRel, string ResourcesRel)[] Candidates =
    [
        (Path.Combine(ClientValues.DataFolder, "StreamingAssets"), Path.Combine(ClientValues.DataFolder, "resources.assets")),
        ("StreamingAssets", "resources.assets"),
        (".", Path.Combine("..", "resources.assets")),
    ];

    public static GameDirLayout Resolve(string path)
    {
        var root = Path.GetFullPath(path);
        var tried = new List<string>();

        foreach (var (streamingRel, resourcesRel) in Candidates)
        {
            var streaming = Path.GetFullPath(Path.Combine(root, streamingRel));
            var index = Path.Combine(streaming, "resource", "matrix", "index");
            tried.Add(index);
            if (!File.Exists(index))
            {
                continue;
            }

            return new GameDirLayout(
                MatrixDirs: MatrixDirs(streaming),
                ResourcesAssetsPath: Path.GetFullPath(Path.Combine(root, resourcesRel)));
        }

        throw new SourceIndexException($"{path} is not a game directory. Tried: " + string.Join(", ", tried));
    }

    private static List<string> MatrixDirs(string streaming)
    {
        var dirs = new List<string> { Path.Combine(streaming, "resource", "matrix") };
        var document = Path.Combine(streaming, "document", "matrix");
        if (Directory.Exists(document))
        {
            if (File.Exists(Path.Combine(document, "index")))
            {
                dirs.Add(document);
            }
            else
            {
                Console.Error.WriteLine($"Ignoring {document}, it has no index file");
            }
        }

        return dirs;
    }
}

/// <summary>
/// A source backed by a local, already-installed game directory.
/// </summary>
public sealed class GameDirSource : ISource
{
    private readonly string _path;
    private readonly GameDirLayout _layout;
    private readonly IBundleIndexReader _bundleIndexReader;

    private Dictionary<string, BundleIndexEntry>? _index;
    private Dictionary<string, string>? _resources;
    private Version? _version;
    private bool _versionRead;

    public GameDirSource(string path, IBundleIndexReader bundleIndexReader)
    {
        _path = path;
        _bundleIndexReader = bundleIndexReader;
        _layout = GameDirResolver.Resolve(path);
    }

    public Task<Version?> SourceVersionAsync(CancellationToken ct = default)
    {
        if (_versionRead)
        {
            return Task.FromResult(_version);
        }

        _versionRead = true;
        try
        {
            var bytes = File.ReadAllBytes(_layout.ResourcesAssetsPath);
            var config = XBuildConfig.Extract(bytes, _bundleIndexReader);
            _version = Versions.Parse(config.DisplayVersion);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"No version from {_layout.ResourcesAssetsPath}: {e.Message}");
        }

        return Task.FromResult(_version);
    }

    public async Task<bool> HasBlobAsync(string blob, CancellationToken ct = default) =>
        (await ResourcesAsync(ct)).ContainsKey(blob);

    public async Task<byte[]> GetBlobAsync(string blob, CancellationToken ct = default)
    {
        var resources = await ResourcesAsync(ct);
        return await File.ReadAllBytesAsync(resources[blob], ct);
    }

    public Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default) =>
        Task.FromResult(Index().TryGetValue(bundle, out var entry) ? entry.Blob : null);

    public Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default) =>
        Task.FromResult(Index().TryGetValue(bundle, out var entry) ? entry.Sha1 : null);

    public Task<IReadOnlyDictionary<string, string>> ResourcesAsync(CancellationToken ct = default)
    {
        if (_resources is not null)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(_resources);
        }

        var resources = new Dictionary<string, string>();
        foreach (var matrixDir in _layout.MatrixDirs)
        {
            if (!Directory.Exists(matrixDir))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFiles(matrixDir))
            {
                resources[Path.GetFileName(entry)] = entry;
            }
        }

        if (File.Exists(_layout.ResourcesAssetsPath))
        {
            resources["resources.assets"] = _layout.ResourcesAssetsPath;
        }

        _resources = resources;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(resources);
    }

    public Task<IReadOnlyCollection<string>> BundleNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(Index().Keys);

    public override string ToString() => $"GameDirSource({_path})";

    private Dictionary<string, BundleIndexEntry> Index()
    {
        if (_index is not null)
        {
            return _index;
        }

        var index = new Dictionary<string, BundleIndexEntry>();
        foreach (var matrixDir in _layout.MatrixDirs)
        {
            var indexPath = Path.Combine(matrixDir, "index");
            var indexFileBytes = File.ReadAllBytes(indexPath);

            var indexBytes = _bundleIndexReader.ReadTextAssetByBasename(indexFileBytes, "index.bytes")
                ?? throw new SourceIndexException($"No index.bytes container in {indexPath}");

            // Later matrix dirs win on collision (precedence order, lowest first).
            foreach (var (bundle, entry) in IndexCodec.ParseIndex(indexBytes))
            {
                index[bundle] = entry;
            }
        }

        _index = index;
        return index;
    }
}
