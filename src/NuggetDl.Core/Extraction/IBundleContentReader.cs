namespace NuggetDl.Core.Extraction;

/// <summary>An asset found in a bundle's container, at its original path.</summary>
public abstract record BundleAsset(string ContainerPath);

/// <summary>A TextAsset's raw script bytes, before TextAssetRewriter is applied.</summary>
public sealed record TextAssetContent(string ContainerPath, byte[] Script) : BundleAsset(ContainerPath);

/// <summary>
/// A Texture2D or Sprite, already decoded to raw RGBA32 pixels (row-major,
/// top-to-bottom - AssetsTools.NET.Texture's decoder handles the underlying
/// format and any platform-specific swizzling).
/// </summary>
public sealed record ImageAssetContent(string ContainerPath, byte[] RgbaPixels, int Width, int Height)
    : BundleAsset(ContainerPath);

/// <summary>A Font object's raw font file bytes (TTF/OTF).</summary>
public sealed record FontAssetContent(string ContainerPath, byte[] FontData) : BundleAsset(ContainerPath);

/// <summary>
/// Loads a bundle's full contents for extraction. Distinct from
/// IBundleIndexReader (which only reads specific known TextAssets/objects by
/// name/path) - this enumerates every extractable asset a bundle contains.
/// Implemented in NuggetDl.UnityInterop via AssetsTools.NET +
/// AssetsTools.NET.Texture.
/// </summary>
public interface IBundleContentReader
{
    /// <summary>Same semantics as IBundleIndexReader.SetDecryptKey - set once
    /// before loading any bundle that might be UnityCN-encrypted.</summary>
    void SetDecryptKey(string? key);

    /// <summary>
    /// Loads and decodes every supported asset (TextAsset, Texture2D, Sprite,
    /// Font) in the bundle's container. Unsupported types are silently
    /// skipped - matches extract_bundle's behavior of only handling a fixed
    /// set of types and ignoring the rest.
    /// </summary>
    IReadOnlyList<BundleAsset> LoadContents(byte[] bundleBytes);
}
