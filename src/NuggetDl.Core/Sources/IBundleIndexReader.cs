namespace NuggetDl.Core.Sources;

/// <summary>
/// The one seam where Core needs Unity asset-file parsing without depending
/// on a specific parsing library. Implemented in NuggetDl.UnityInterop via
/// AssetsTools.NET.
/// </summary>
public interface IBundleIndexReader
{
    /// <summary>
    /// Sets the UnityCN asset-bundle decrypt key (16 bytes when UTF-8
    /// encoded) used for any bundle loaded from this point on that turns out
    /// to be encrypted. Pass null/empty to clear it. Bundles that aren't
    /// encrypted don't need this set at all - resources.assets in particular
    /// is a bare serialized file, never a bundle, so it's never affected by
    /// this regardless of whether a key is set.
    /// </summary>
    void SetDecryptKey(string? key);

    /// <summary>
    /// Loads the bundle from <paramref name="bundleBytes"/> and returns the
    /// raw bytes of the first TextAsset found at any of
    /// <paramref name="candidatePaths"/> in the bundle's container.
    /// Returns null if none of the candidate paths exist in the bundle.
    /// </summary>
    byte[]? ReadTextAsset(byte[] bundleBytes, IReadOnlyList<string> candidatePaths);

    /// <summary>
    /// Loads the bundle from <paramref name="bundleBytes"/> and returns the
    /// raw bytes of the first TextAsset whose container path's last segment
    /// (after the final '/') equals <paramref name="basename"/> - used where
    /// the directory prefix has moved between builds (e.g. assets/temp vs
    /// assets/buildtemp) but the filename hasn't. Returns null if no such
    /// TextAsset exists.
    /// </summary>
    byte[]? ReadTextAssetByBasename(byte[] bundleBytes, string basename);

    /// <summary>
    /// Loads the asset file (bundle or serialized file, e.g. resources.assets)
    /// from <paramref name="assetFileBytes"/> and returns the raw serialized
    /// bytes of the first object whose name equals <paramref name="objectName"/>.
    /// Returns null if no such object exists.
    /// </summary>
    byte[]? ReadObjectRawDataByName(byte[] assetFileBytes, string objectName);
}
