using AssetsTools.NET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Crops a decoded Texture2D down to one packed Sprite's region and applies
/// its packing rotation/flip.
/// </summary>
public static class SpriteCropping
{
    private readonly record struct RectF(float X, float Y, float Width, float Height);

    /// <summary>
    /// Takes the resolved SpriteRenderData struct itself (either the Sprite's
    /// own m_RD, or a SpriteAtlas's m_RenderDataMap entry - see
    /// UnityBundleContentReader.ResolveSpriteRenderData), not the Sprite
    /// object, since both sources expose the same texture/textureRect/
    /// settingsRaw shape once resolved.
    ///
    /// NOT COVERED:
    ///  - Tight packing (kSPMTight)'s mesh-based masking/UV remap - needs a
    ///    Unity Mesh format parser that doesn't
    ///    exist here. A tight-packed sprite still gets the correct
    ///    rectangular crop and rotation, just without the polygon mask/UV
    ///    remap trimming it to the sprite's actual silhouette.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Crop(
        byte[] textureRgba, int textureWidth, int textureHeight, AssetTypeValueField rd)
    {
        var rect = ReadRect(rd["textureRect"]);
        var settingsRaw = rd["settingsRaw"].AsUInt;

        using var full = Image.LoadPixelData<Rgba32>(textureRgba, textureWidth, textureHeight);

        // Unity's texture space has its origin at the bottom-left; the RGBA
        // buffer here is already top-down (proven correct earlier against
        // real textures - foam.png's gradient direction matched expectations
        // with no extra flip applied), so the crop rect's Y needs converting
        // once here rather than flipping the whole image at the end the way
        // the Python version does over its bottom-up source.
        var cropX = (int)Math.Round(rect.X);
        var cropY = (int)Math.Round(textureHeight - rect.Y - rect.Height);
        var cropWidth = (int)Math.Round(rect.Width);
        var cropHeight = (int)Math.Round(rect.Height);

        cropX = Math.Clamp(cropX, 0, Math.Max(0, textureWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, textureHeight - 1));
        cropWidth = Math.Clamp(cropWidth, 1, textureWidth - cropX);
        cropHeight = Math.Clamp(cropHeight, 1, textureHeight - cropY);

        full.Mutate(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropWidth, cropHeight)));

        var packed = (settingsRaw & 1) != 0;
        if (packed)
        {
            var packingRotation = (settingsRaw >> 2) & 0xF;

            // Rotation as a raster operation doesn't depend on the top-down/bottom-up
            // origin distinction the crop rect's Y did, so these port
            // directly (kSPRRotate90 -> Rotate270 is UnityPy's own mapping, not a mistake).
            switch (packingRotation)
            {
                case 1: // kSPRFlipHorizontal
                    full.Mutate(ctx => ctx.Flip(FlipMode.Horizontal));
                    break;
                case 2: // kSPRFlipVertical
                    full.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
                    break;
                case 3: // kSPRRotate180
                    full.Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                    break;
                case 4: // kSPRRotate90 (mapped to a 270-degree rotate, matching UnityPy)
                    full.Mutate(ctx => ctx.Rotate(RotateMode.Rotate270));
                    break;
            }
        }

        var pixels = new byte[full.Width * full.Height * 4];
        full.CopyPixelDataTo(pixels);
        return (pixels, full.Width, full.Height);
    }

    private static RectF ReadRect(AssetTypeValueField rect) => new(
        rect["x"].AsFloat, rect["y"].AsFloat, rect["width"].AsFloat, rect["height"].AsFloat);
}
