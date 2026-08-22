namespace NuggetDl.Core;

public static class AssetPaths
{
    public static readonly string[] ThumbnailImageMarkers = ["/image/rolecharacter/", "/image/rolestory/"];
    public const int ThumbnailSize = 256;

    /// <summary>Bundle-name markers used by the --all-temp / --all-images selection filters.</summary>
    public const string TempBundleMarker = "assets/temp/";
    public const string TextureBundleMarker = "assets/product/texture/";
}
