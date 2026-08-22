using AssetsTools.NET;
using AssetsTools.NET.Extra;
using NuggetDl.Core.Extraction;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Implements IBundleContentReader using AssetsTools.NET + AssetsTools.NET.Texture.
/// Texture decoding itself lives in TextureDecoding.cs, Sprite rect-crop/rotation
/// in SpriteCropping.cs (both shared with SpineExtractor where relevant).
///
/// KNOWN GAP - Sprite atlas cropping (see SpriteCropping.cs remarks for the
/// one piece still missing: tight-packing's mesh-based mask/UV remap). The
/// common default-packing rect crop + rotation/flip case is handled, for
/// both a Sprite's own m_RD and the SpriteAtlas m_RenderDataMap indirection.
/// </summary>
public sealed class UnityBundleContentReader : IBundleContentReader
{
    private byte[]? _decryptKeyBytes;

    public void SetDecryptKey(string? key)
    {
        _decryptKeyBytes = string.IsNullOrEmpty(key) ? null : System.Text.Encoding.UTF8.GetBytes(key);
    }

    public IReadOnlyList<BundleAsset> LoadContents(byte[] bundleBytes)
    {
        var am = new AssetsManager();
        AssetsFileInstance afileInst;
        IReadOnlyDictionary<string, byte[]> siblingEntries;

        if (EncryptedBundleReader.IsEncryptedUnityFsBundle(bundleBytes))
        {
            if (_decryptKeyBytes is null)
            {
                throw new InvalidOperationException(
                    "Bundle is UnityCN-encrypted but no decrypt key has been set "
                    + "(call IBundleContentReader.SetDecryptKey first).");
            }

            var entries = EncryptedBundleReader.Read(bundleBytes, _decryptKeyBytes);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("Encrypted bundle had no directory entries after decryption");
            }

            var byPath = new Dictionary<string, byte[]>();
            foreach (var e in entries)
            {
                byPath[e.Path] = e.Data;
            }

            siblingEntries = byPath;
            afileInst = am.LoadAssetsFile(new MemoryStream(entries[0].Data), entries[0].Path, true);
        }
        else
        {
            var stream = new MemoryStream(bundleBytes);
            var bundleInst = am.LoadBundleFile(stream, "bundle", true);
            afileInst = am.LoadAssetsFileFromBundle(bundleInst, 0, true);
            siblingEntries = new Dictionary<string, byte[]>();
        }

        var afile = afileInst.file;
        var containerMap = BuildContainerPathMap(am, afileInst);

        var textAssetIds = afile.GetAssetsOfType(AssetClassID.TextAsset).Select(i => i.PathId).ToHashSet();
        var texture2DIds = afile.GetAssetsOfType(AssetClassID.Texture2D).Select(i => i.PathId).ToHashSet();
        var spriteIds = afile.GetAssetsOfType(AssetClassID.Sprite).Select(i => i.PathId).ToHashSet();

        AssetTypeValueField? firstFontField = null;
        var fontLookedUp = false;

        var results = new List<BundleAsset>(containerMap.Count);

        foreach (var (path, info) in containerMap)
        {
            try
            {
                if (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                {
                    // Mirrors extract_bundle exactly: grabs the first Font-typed
                    // object in the whole file, not necessarily the one `info`
                    // points at - odd, but that's what the Python does.
                    if (!fontLookedUp)
                    {
                        fontLookedUp = true;
                        var fontInfo = afile.GetAssetsOfType(AssetClassID.Font).FirstOrDefault();
                        firstFontField = fontInfo is not null ? am.GetBaseField(afileInst, fontInfo) : null;
                    }

                    if (firstFontField is not null)
                    {
                        var fontData = firstFontField["m_FontData"].AsByteArray;
                        results.Add(new FontAssetContent(path, fontData));
                    }

                    continue;
                }

                if (textAssetIds.Contains(info.PathId))
                {
                    var baseField = am.GetBaseField(afileInst, info);
                    results.Add(new TextAssetContent(path, baseField["m_Script"].AsByteArray));
                    continue;
                }

                if (texture2DIds.Contains(info.PathId))
                {
                    var image = TextureDecoding.DecodeTexture2D(am, afileInst, info, siblingEntries);
                    if (image is not null)
                    {
                        results.Add(new ImageAssetContent(path, image.Value.Pixels, image.Value.Width, image.Value.Height));
                    }

                    continue;
                }

                if (spriteIds.Contains(info.PathId))
                {
                    var spriteBaseField = am.GetBaseField(afileInst, info);
                    var renderData = ResolveSpriteRenderData(afile, am, afileInst, spriteBaseField);
                    if (renderData is not null)
                    {
                        var fullTexture = TextureDecoding.DecodeTexture2D(am, afileInst, renderData.Value.TextureInfo, siblingEntries);
                        if (fullTexture is not null)
                        {
                            var (pixels, width, height) = SpriteCropping.Crop(
                                fullTexture.Value.Pixels, fullTexture.Value.Width, fullTexture.Value.Height,
                                renderData.Value.Field);
                            results.Add(new ImageAssetContent(path, pixels, width, height));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Unexpected failure reading {path}: {e.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a Sprite's render data - either its own m_RD (unpacked /
    /// legacy-packed sprites) or, if m_RD.texture is empty (the modern
    /// SpriteAtlas system), the matching entry in the referenced SpriteAtlas's
    /// m_RenderDataMap. Matched by structural comparison of the GUID+long key
    /// rather than assuming specific field names, since that key's exact
    /// serialized field names weren't confirmed via probing.
    /// </summary>
    private static (AssetTypeValueField Field, AssetFileInfo TextureInfo)? ResolveSpriteRenderData(
        AssetsFile afile, AssetsManager am, AssetsFileInstance afileInst, AssetTypeValueField spriteBaseField)
    {
        var rd = spriteBaseField.Children.FirstOrDefault(c => c.FieldName == "m_RD");
        var directTexField = rd?.Children.FirstOrDefault(c => c.FieldName == "texture");
        var directTexPathId = directTexField?.Children.FirstOrDefault(c => c.FieldName == "m_PathID")?.AsLong ?? 0;
        if (directTexPathId != 0)
        {
            var directInfo = afile.GetAssetInfo(directTexPathId);
            return directInfo is not null && rd is not null ? (rd, directInfo) : null;
        }

        var spriteAtlasField = spriteBaseField.Children.FirstOrDefault(c => c.FieldName == "m_SpriteAtlas");
        var atlasPathId = spriteAtlasField?.Children.FirstOrDefault(c => c.FieldName == "m_PathID")?.AsLong ?? 0;
        if (atlasPathId == 0)
        {
            return null;
        }

        var atlasInfo = afile.GetAssetInfo(atlasPathId);
        if (atlasInfo is null)
        {
            return null;
        }

        var atlas = am.GetBaseField(afileInst, atlasInfo);
        var renderDataKey = spriteBaseField.Children.FirstOrDefault(c => c.FieldName == "m_RenderDataKey");
        var mapField = atlas.Children.FirstOrDefault(c => c.FieldName == "m_RenderDataMap");
        var map = mapField?.Children.FirstOrDefault(c => c.FieldName == "Array");
        if (map is null || renderDataKey is null)
        {
            return null;
        }

        foreach (var entry in map.Children)
        {
            var keyField = entry.Children.FirstOrDefault(c => c.FieldName == "first");
            var valueField = entry.Children.FirstOrDefault(c => c.FieldName == "second");
            if (keyField is null || valueField is null || !FieldsEqual(keyField, renderDataKey))
            {
                continue;
            }

            var texField = valueField.Children.FirstOrDefault(c => c.FieldName == "texture");
            var texPathId = texField?.Children.FirstOrDefault(c => c.FieldName == "m_PathID")?.AsLong ?? 0;
            var texInfo = texPathId == 0 ? null : afile.GetAssetInfo(texPathId);
            return texInfo is not null ? (valueField, texInfo) : null;
        }

        return null;
    }

    /// <summary>Structural equality over leaf values - used to match a
    /// Sprite's m_RenderDataKey against SpriteAtlas.m_RenderDataMap entries
    /// without depending on the GUID sub-field names.</summary>
    private static bool FieldsEqual(AssetTypeValueField a, AssetTypeValueField b)
    {
        if (a.Children.Count != b.Children.Count)
        {
            return false;
        }

        if (a.Children.Count == 0)
        {
            try
            {
                return a.AsLong == b.AsLong;
            }
            catch
            {
                return a.AsString == b.AsString;
            }
        }

        for (var i = 0; i < a.Children.Count; i++)
        {
            if (!FieldsEqual(a.Children[i], b.Children[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, AssetFileInfo> BuildContainerPathMap(
        AssetsManager am, AssetsFileInstance afileInst)
    {
        var map = new Dictionary<string, AssetFileInfo>();
        var afile = afileInst.file;

        var bundleInfo = afile.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault();
        if (bundleInfo is null)
        {
            return map;
        }

        var bundleField = am.GetBaseField(afileInst, bundleInfo);
        var container = bundleField["m_Container.Array"];

        foreach (var entry in container.Children)
        {
            var path = entry[0].AsString;
            var pathId = entry[1]["asset.m_PathID"].AsLong;

            var targetInfo = afile.GetAssetInfo(pathId);
            if (targetInfo is not null)
            {
                map[path] = targetInfo;
            }
        }

        return map;
    }
}
