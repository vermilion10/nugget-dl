using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NuggetDl.Core.Extraction;

/// <summary>
/// Extracts every supported asset out of a bundle to disk. 
/// </summary>
public static class BundleExtractor
{
    public static void ExtractBundle(
        IReadOnlyList<BundleAsset> assets,
        string outputDir,
        Version gameVersion,
        bool allowBinaryTableConvert = true,
        Action<string>? onExtracted = null)
    {
        foreach (var asset in assets)
        {
            try
            {
                switch (asset)
                {
                    case TextAssetContent text:
                        onExtracted?.Invoke(
                            ExtractTextAsset(text, outputDir, gameVersion, allowBinaryTableConvert));
                        break;

                    case ImageAssetContent image:
                        onExtracted?.Invoke(ExtractImage(image, outputDir));
                        break;

                    case FontAssetContent font:
                        ExtractFont(font, outputDir);
                        onExtracted?.Invoke(font.ContainerPath);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Unexpected failure extracting {asset.ContainerPath}: {e.Message}");
            }
        }
    }

    /// <summary>Returns the actual (possibly rewritten - .bytes stripped,
    /// lua decrypted, or converted to .csv) destination container path, for
    /// accurate logging.</summary>
    private static string ExtractTextAsset(
        TextAssetContent text, string outputDir, Version gameVersion, bool allowBinaryTableConvert)
    {
        var rewritten = TextAssetRewriter.Rewrite(text.ContainerPath, text.Script, gameVersion, allowBinaryTableConvert);
        var dest = DestinationPath(outputDir, rewritten.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, rewritten.Data);
        return rewritten.Path;
    }

    private static void ExtractFont(FontAssetContent font, string outputDir)
    {
        var dest = DestinationPath(outputDir, font.ContainerPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, font.FontData);
    }

    /// <summary>Returns the actual .png destination path written, for accurate logging.</summary>
    private static string ExtractImage(ImageAssetContent image, string outputDir)
    {
        var dest = DestinationPath(outputDir, image.ContainerPath);
        var destWithoutExt = Path.Combine(
            Path.GetDirectoryName(dest) ?? string.Empty,
            Path.GetFileNameWithoutExtension(dest));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var img = Image.LoadPixelData<Rgba32>(image.RgbaPixels, image.Width, image.Height);

        img.SaveAsPng(destWithoutExt + ".png");
        img.Save(destWithoutExt + ".webp", new WebpEncoder { Quality = 80, FileFormat = WebpFileFormatType.Lossy });

        var normalized = destWithoutExt.Replace(Path.DirectorySeparatorChar, '/');
        if (AssetPaths.ThumbnailImageMarkers.Any(marker => normalized.Contains(marker)))
        {
            using var thumb = img.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(AssetPaths.ThumbnailSize, AssetPaths.ThumbnailSize),
            }));

            thumb.Save(
                $"{destWithoutExt}.{AssetPaths.ThumbnailSize}.webp",
                new WebpEncoder { Quality = 80, FileFormat = WebpFileFormatType.Lossy });
        }

        return destWithoutExt + ".png";
    }

    private static string DestinationPath(string outputDir, string containerPath) =>
        Path.Combine(outputDir, Path.Combine(containerPath.Split('/')));
}
