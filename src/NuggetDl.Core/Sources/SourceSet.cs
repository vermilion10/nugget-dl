namespace NuggetDl.Core.Sources;

/// <summary>
/// Layers a primary source (game install, OBB, or PC starter) with a patch
/// CDN source on top.
/// </summary>
public sealed class SourceSet
{
    private readonly IBundleIndexReader _bundleIndexReader;
    public List<ISource> Sources { get; } = [];

    public SourceSet(IBundleIndexReader bundleIndexReader)
    {
        _bundleIndexReader = bundleIndexReader;
    }

    /// <summary>
    /// Adds the primary (base game) source: an OBB file (primaryType "obb"),
    /// a local install directory (primaryType "local"), or a named PC
    /// starter CDN (e.g. "EN_PC").
    /// </summary>
    public Task AddPrimaryAsync(
        string primaryType, string? obb, bool prerelease, string? gameDir = null, CancellationToken ct = default)
    {
        switch (primaryType)
        {
            case "obb":
                if (obb is null)
                {
                    throw new ArgumentException("--obb is required when primary is 'obb'", nameof(obb));
                }

                Sources.Add(new ObbSource(obb, _bundleIndexReader));
                break;

            case "local":
                if (gameDir is null)
                {
                    throw new ArgumentException("--game-dir is required when primary is 'local'", nameof(gameDir));
                }

                Sources.Add(new GameDirSource(gameDir, _bundleIndexReader));
                break;

            default:
                if (!PcStarterCdn.ByName.TryGetValue(primaryType, out var cdnData))
                {
                    throw new UnknownSourceException(
                        ClientValues.IsConfigured ? $"Unknown primary type {primaryType}" : ClientValues.MissingMessage);
                }

                Sources.Add(new PcStarterSource(cdnData, primaryType, prerelease, _bundleIndexReader));
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a patch CDN source. If <paramref name="version"/> is null, it's
    /// inferred from whatever sources are already in the set (patch never
    /// carries a .patch component even if the PC primary source does).
    /// </summary>
    public async Task AddPatchAsync(
        string patchType, string? version, CancellationToken ct = default)
    {
        if (!PatchCdn.ByName.TryGetValue(patchType, out var cdnData))
        {
            throw new UnknownSourceException(
                        ClientValues.IsConfigured ? $"Unknown patch type {patchType}" : ClientValues.MissingMessage);
        }

        version ??= await InferPatchVersionAsync(ct);
        if (version is null)
        {
            throw new PatchVersionRequiredException();
        }

        string? key = null;
        if (Versions.Parse(version) >= Versions.PatchKeySchemeMinVersion)
        {
            var resourcesAssets = await ResourcesAssetsBytesAsync(ct);
            key = XBuildConfig.ExtractBuildKey(resourcesAssets, _bundleIndexReader);
        }

        var source = await PatchCdnSource.CreateAsync(
            patchType, cdnData, version, _bundleIndexReader, key, ct);
        Sources.Add(source);
    }

    private async Task<string?> InferPatchVersionAsync(CancellationToken ct)
    {
        var inferred = await VersionAsync(ct);
        if (inferred is null)
        {
            return null;
        }

        // Patch never has a .patch (fourth/build) component even if the
        // primary (e.g. PC) source does - zero it out, keep major.minor.build.
        return $"{inferred.Major}.{inferred.Minor}.0";
    }

    public async Task<Version?> VersionAsync(CancellationToken ct = default)
    {
        foreach (var source in Sources)
        {
            var version = await source.SourceVersionAsync(ct);
            if (version is not null)
            {
                return version;
            }
        }

        return null;
    }

    private async Task<byte[]> ResourcesAssetsBytesAsync(CancellationToken ct)
    {
        foreach (var source in Sources)
        {
            if (await source.HasBlobAsync("resources.assets", ct))
            {
                return await source.GetBlobAsync("resources.assets", ct);
            }
        }

        throw new BlobNotFoundException(
            "resources.assets not found in any source (required for >=4.3.0 patch key)");
    }

    public async Task<HashSet<string>> ListAllBundlesAsync(CancellationToken ct = default)
    {
        var all = new HashSet<string>();
        foreach (var source in Sources)
        {
            all.UnionWith(await source.BundleNamesAsync(ct));
        }

        return all;
    }

    /// <summary>Pre-fetches each source's index/resources so later lookups are cache hits.</summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        foreach (var source in Sources)
        {
            await source.BundleNamesAsync(ct);
            await source.ResourcesAsync(ct);
        }
    }

    public async Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default)
    {
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            var blob = await Sources[i].BundleToBlobAsync(bundle, ct);
            if (blob is not null)
            {
                return blob;
            }
        }

        return null;
    }

    public async Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default)
    {
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            var sha1 = await Sources[i].BundleSha1Async(bundle, ct);
            if (sha1 is not null)
            {
                return sha1;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a bundle name to its containing blob (using the last source
    /// that knows about it), then downloads that blob from the first source
    /// that actually hosts it.
    /// </summary>
    public async Task<byte[]> FindBundleAsync(string bundle, CancellationToken ct = default)
    {
        var blob = await BundleToBlobAsync(bundle, ct);
        if (blob is null)
        {
            throw new BlobNotFoundException($"Failed to resolve bundle {bundle}");
        }

        var foundInSource = false;
        Exception? lastError = null;

        foreach (var source in Sources)
        {
            if (!await source.HasBlobAsync(blob, ct))
            {
                continue;
            }

            foundInSource = true;
            try
            {
                return await source.GetBlobAsync(blob, ct);
            }
            catch (Exception e)
            {
                lastError = e;
                Console.Error.WriteLine($"Failed to get blob {blob} from {source}: {e.Message}");
            }
        }

        if (foundInSource)
        {
            throw new BlobDownloadException(
                $"Blob {blob} is hosted but could not be downloaded from any source", lastError!);
        }

        throw new BlobNotFoundException($"Failed to resolve blob {blob}");
    }
}
