using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Shared Texture2D pixel decoding, used by both UnityBundleContentReader
/// (extracting a texture directly) and SpineExtractor (resolving a
/// Material's referenced texture). Extracted out of UnityBundleContentReader
/// so both share one code path rather than diverging copies.
///
/// Streamed (.resS) texture data in encrypted bundles is resolved manually
/// via ResolveStreamedPictureData, matching against EncryptedBundleReader's
/// full directory entry list (AssetsTools.NET's own FillPictureData resource
/// resolution assumes a BundleFileInstance backing, which the encrypted path
/// doesn't have). Verified correct at runtime 2026-08-20 against real
/// streamed textures.
/// </summary>
public static class TextureDecoding
{
    public static (byte[] Pixels, int Width, int Height)? DecodeTexture2D(
        AssetsManager am, AssetsFileInstance afileInst, AssetFileInfo info,
        IReadOnlyDictionary<string, byte[]> siblingEntries)
    {
        var baseField = am.GetBaseField(afileInst, info);
        var texture = TextureFile.ReadTextureFile(baseField);

        var pictureData = texture.FillPictureData(afileInst);
        if ((pictureData is null || pictureData.Length == 0) && !string.IsNullOrEmpty(texture.m_StreamData.path))
        {
            pictureData = ResolveStreamedPictureData(texture, siblingEntries);
        }

        if (pictureData is null || pictureData.Length == 0)
        {
            Console.Error.WriteLine($"No picture data for texture (streamed path '{texture.m_StreamData.path}')");
            return null;
        }

        var rgba = texture.DecodeTextureRaw(pictureData, useBgra: false);
        if (rgba is null)
        {
            return null;
        }

        return (rgba, texture.m_Width, texture.m_Height);
    }

    private static byte[]? ResolveStreamedPictureData(
        TextureFile texture, IReadOnlyDictionary<string, byte[]> siblingEntries)
    {
        var wantedName = texture.m_StreamData.path.Split('/')[^1];
        foreach (var (entryPath, data) in siblingEntries)
        {
            if (entryPath.Split('/')[^1] != wantedName)
            {
                continue;
            }

            var offset = (long)texture.m_StreamData.offset;
            var size = (long)texture.m_StreamData.size;
            if (offset + size > data.Length)
            {
                return null;
            }

            var slice = new byte[size];
            Array.Copy(data, offset, slice, 0, size);
            return slice;
        }

        return null;
    }
}
