namespace NuggetDl.Core.Sources;

/// <summary>
/// A single origin of game data: a CDN, a local OBB/APK, or a game install
/// directory. <see cref="SourceSet"/> layers several of these together.
///
/// Unlike the Python Source ABC (where index()/get_blob() are synchronous
/// because requests.Session blocks), every method that may need to hit the
/// network is async here. Implementations that never need I/O (e.g. a local
/// game-directory source) can just return completed tasks.
/// </summary>
public interface ISource
{
    /// <summary>Returns true if the source has the blob at the given path.</summary>
    Task<bool> HasBlobAsync(string blob, CancellationToken ct = default);

    /// <summary>Returns the blob at the given path as binary data.</summary>
    Task<byte[]> GetBlobAsync(string blob, CancellationToken ct = default);

    /// <summary>Returns the sha1 of the given bundle, or null if unknown.</summary>
    Task<string?> BundleSha1Async(string bundle, CancellationToken ct = default);

    /// <summary>Returns the blob that contains the bundle, or null if unknown.</summary>
    Task<string?> BundleToBlobAsync(string bundle, CancellationToken ct = default);

    /// <summary>Returns the version of the source, or null if unknown.</summary>
    Task<Version?> SourceVersionAsync(CancellationToken ct = default);

    /// <summary>Returns a map of blob name to path/URL.</summary>
    Task<IReadOnlyDictionary<string, string>> ResourcesAsync(CancellationToken ct = default);

    /// <summary>Returns all bundle names known to this source.</summary>
    Task<IReadOnlyCollection<string>> BundleNamesAsync(CancellationToken ct = default);
}
