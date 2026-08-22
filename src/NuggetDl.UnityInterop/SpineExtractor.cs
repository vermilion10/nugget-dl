using AssetsTools.NET;
using AssetsTools.NET.Extra;
using NuggetDl.Core.Spine;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Discovers and extracts Spine skeletons from a bundle's assets file.
///
/// ARCHITECTURE NOTE: UnityPy's crawl() discovers Spine components by
/// walking the object graph from the bundle's container roots, chasing
/// PPtr references. That approach was tried here first and failed silently
/// in practice (2026-08-20, sailika.ab): a bundle's container generally only
/// lists top-level named assets, and the GameObject actually carrying the
/// SkeletonGraphic component isn't always one of them, so container-rooted
/// traversal can simply never reach it. Discovery here instead scans every
/// MonoBehaviour in the file directly via GetAssetsOfType(MonoBehaviour) -
/// the same approach SpineProbe used to confirm typetree access works at
/// all - which is both simpler and doesn't depend on the container listing
/// containing the right root. The targeted extraction that follows
/// (HandleSkeleton walking a known GameObject's m_Component,
/// WalkObjectChildren's BFS for bone followers) still needs the PPtr/array/
/// pair-struct heuristics below, since AssetsTools.NET has no typed object
/// model to fall back on for that generic navigation:
///  - "is this field a PPtr" - has direct m_FileID + m_PathID children
///  - "is this field an array" - has a single child literally named "Array"
///  - "is this field a pair struct" (matches m_Component-style entries,
///    proven correct earlier for AssetBundle's m_Container) - has "first"
///    and "second" children
/// </summary>
public sealed class SpineExtractor
{
    private readonly AssetsManager _am;

    /// <summary>
    /// Every loaded assets file's sibling entries (its .resS payload), keyed
    /// by AssetsFileInstance.name (the CAB name), for texture decoding.
    ///
    /// Keyed per file rather than held as one dictionary because a spine is
    /// frequently split across SEVERAL bundles, each with its own .resS
    /// (confirmed 2026-08-22, qishitutorial.ab): the prefab lives in one
    /// bundle while the skeleton data/atlas/texture it points at live in
    /// sibling bundles, reached through a non-zero m_FileID naming another
    /// CAB via the assets file's externals table.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> _siblingEntriesByFile;
    private readonly SpineGroup _spine;

    private SpineExtractor(
        AssetsManager am,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> siblingEntriesByFile,
        string name)
    {
        _am = am;
        _siblingEntriesByFile = siblingEntriesByFile;
        _spine = new SpineGroup(name);
    }

    /// <summary>A resolved object together with the file it actually lives
    /// in. The file has to travel with the field: any further PPtr read off
    /// that object resolves relative to ITS file's externals table, not the
    /// file we started the chase from.</summary>
    private readonly record struct Ref(AssetTypeValueField Field, AssetFileInfo Info, AssetsFileInstance File);

    /// <summary>
    /// Extracts one Spine group, writing output if any skeletons were found.
    /// Every file in <paramref name="files"/> is scanned for spine
    /// components, so gluing several bundles together is just a matter of
    /// passing them all in.
    ///
    /// globalScaleRoot is the prefab GameObject that CheckGlobalScale runs
    /// against - extract_spine uses obj[-1], the last root object it was
    /// handed, and those roots are prefab GameObjects in the Python CLI.
    /// </summary>
    public static void Extract(
        string name,
        IReadOnlyList<AssetsFileInstance> files,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> siblingEntriesByFile,
        (AssetsFileInstance File, AssetFileInfo Info)? globalScaleRoot,
        AssetsManager am,
        string outputDir)
    {
        var extractor = new SpineExtractor(am, siblingEntriesByFile, name);

        foreach (var file in files)
        {
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                try
                {
                    var field = am.GetBaseField(file, info);
                    extractor.RecordSpineComponent(field, file);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Failed to read MonoBehaviour {info.PathId}: {e.Message}");
                }
            }
        }

        if (globalScaleRoot is { } root)
        {
            var (globalScale, scalePathIds) = extractor.CheckGlobalScale(root.Info, root.File);
            if (scalePathIds is not null && globalScale is not null)
            {
                var scale = globalScale.Value;
                if (scale > 2)
                {
                    scale /= 100;
                }

                if (scale != 1)
                {
                    if (extractor._spine.Spines.Any(x => scalePathIds.Contains(x.TransformId)))
                    {
                        // Found spine with global scale itself, skip applying it again.
                    }
                    else
                    {
                        foreach (var spineInfo in extractor._spine.Spines)
                        {
                            spineInfo.SetScale(spineInfo.Scale * scale);
                        }
                    }
                }
            }
        }

        extractor._spine.Finalize_();
        SpineQuirks.ApplyQuirk(extractor._spine);

        if (extractor._spine.Spines.Count > 0)
        {
            extractor._spine.Write(outputDir);
        }
        else
        {
            Console.Error.WriteLine($"Processed {name} but no spines found");
        }
    }

    // ---- Field-shape heuristics ----------------------------------------

    private static bool TryGetPPtrIds(AssetTypeValueField field, out long fileId, out long pathId)
    {
        fileId = 0;
        pathId = 0;

        AssetTypeValueField? fileIdField = null;
        AssetTypeValueField? pathIdField = null;
        foreach (var child in field.Children)
        {
            if (child.FieldName == "m_FileID")
            {
                fileIdField = child;
            }
            else if (child.FieldName == "m_PathID")
            {
                pathIdField = child;
            }
        }

        if (fileIdField is null || pathIdField is null)
        {
            return false;
        }

        fileId = fileIdField.AsLong;
        pathId = pathIdField.AsLong;
        return true;
    }

    private static AssetTypeValueField? TryGetArrayChildren(AssetTypeValueField field)
    {
        foreach (var child in field.Children)
        {
            if (child.FieldName == "Array")
            {
                return child;
            }
        }

        return null;
    }

    private static AssetTypeValueField? TryGetPairSecond(AssetTypeValueField field)
    {
        AssetTypeValueField? first = null;
        AssetTypeValueField? second = null;
        foreach (var child in field.Children)
        {
            if (child.FieldName == "first")
            {
                first = child;
            }
            else if (child.FieldName == "second")
            {
                second = child;
            }
        }

        return first is not null && second is not null ? second : null;
    }

    /// <summary>
    /// Reduces an array element down to a PPtr field, whatever shape it's
    /// wrapped in. Confirmed empirically (2026-08-20, sailika.ab): a plain
    /// `vector&lt;PPtr&lt;Component&gt;&gt;` field like m_Component wraps each
    /// element as data -&gt; component -&gt; {m_FileID, m_PathID} - a DIFFERENT
    /// shape than a genuine `map&lt;K,V&gt;` field like m_Container, which uses a
    /// {first, second} pair (see TryGetPairSecond). This checks known wrapper
    /// names one level at a time and recurses, so it covers both shapes (and
    /// a bare, unwrapped PPtr) without needing to know which one applies -
    /// m_Children's exact wrapper shape specifically hasn't been confirmed
    /// the same way m_Component's was, so this is what makes that path safe
    /// without a second probing round.
    /// </summary>
    private static AssetTypeValueField? UnwrapArrayElement(AssetTypeValueField entry)
    {
        if (TryGetPPtrIds(entry, out _, out _))
        {
            return entry;
        }

        var second = TryGetPairSecond(entry);
        if (second is not null)
        {
            return TryGetPPtrIds(second, out _, out _) ? second : UnwrapArrayElement(second);
        }

        foreach (var wrapperName in new[] { "component", "data" })
        {
            var child = entry.Children.FirstOrDefault(c => c.FieldName == wrapperName);
            if (child is not null)
            {
                return TryGetPPtrIds(child, out _, out _) ? child : UnwrapArrayElement(child);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a PPtr relative to the file that contains it. A non-zero
    /// m_FileID means the target lives in ANOTHER assets file, named by
    /// relativeTo's externals table - handing that off to
    /// AssetsManager.GetExtAsset resolves it as long as the sibling file is
    /// loaded in the same manager (verified 2026-08-22 against
    /// qishitutorial.ab's fileId=2/3 references).
    ///
    /// The old version dropped m_FileID and always looked pathId up in the
    /// single loaded file, so every cross-bundle reference either found
    /// nothing or, worse, silently hit an unrelated local asset that happened
    /// to share the pathId.
    /// </summary>
    private Ref? TryResolvePPtr(AssetTypeValueField pptrField, AssetsFileInstance relativeTo)
    {
        if (!TryGetPPtrIds(pptrField, out var fileId, out var pathId) || pathId == 0)
        {
            return null;
        }

        try
        {
            if (fileId != 0)
            {
                var ext = _am.GetExtAsset(relativeTo, (int)fileId, pathId);
                if (ext.baseField is null || ext.info is null || ext.file is null)
                {
                    return null;
                }

                return new Ref(ext.baseField, ext.info, ext.file);
            }

            var info = relativeTo.file.GetAssetInfo(pathId);
            if (info is null)
            {
                return null;
            }

            return new Ref(_am.GetBaseField(relativeTo, info), info, relativeTo);
        }
        catch
        {
            return null;
        }
    }

    // ---- Spine-specific discovery/extraction --------------------------

    private string? GetMonoScriptClassName(AssetTypeValueField monoBehaviourField, AssetsFileInstance file)
    {
        var scriptField = monoBehaviourField.Children.FirstOrDefault(c => c.FieldName == "m_Script");
        if (scriptField is null)
        {
            return null;
        }

        var resolved = TryResolvePPtr(scriptField, file);
        if (resolved is null)
        {
            return null;
        }

        var classNameField = resolved.Value.Field.Children.FirstOrDefault(c => c.FieldName == "m_ClassName");
        return classNameField?.AsString;
    }

    private void RecordSpineComponent(AssetTypeValueField obj, AssetsFileInstance file)
    {
        var fieldNames = obj.Children.Select(c => c.FieldName).ToHashSet();
        var className = GetMonoScriptClassName(obj, file);

        if (fieldNames.Contains("skeletonDataAsset"))
        {
            var goField = obj.Children.FirstOrDefault(c => c.FieldName == "m_GameObject");
            if (goField is null)
            {
                return;
            }

            var resolvedGo = TryResolvePPtr(goField, file);
            if (resolvedGo is null)
            {
                return;
            }

            var isActiveField = resolvedGo.Value.Field.Children.FirstOrDefault(c => c.FieldName == "m_IsActive");
            var isActive = isActiveField?.AsBool ?? true;
            if (!isActive)
            {
                return;
            }

            var skeleton = HandleSkeleton(resolvedGo.Value.Field, resolvedGo.Value.Info, resolvedGo.Value.File);
            if (skeleton is not null)
            {
                _spine.Spines.Add(skeleton);
            }
        }
        else if (className == "BoneFollowerGraphic")
        {
            var follower = HandleBoneFollower(obj, file);
            if (follower is not null)
            {
                _spine.BoneFollowers.Add(follower);
            }
        }
        else if (className == "UiObject")
        {
            var objListField = obj.Children.FirstOrDefault(c => c.FieldName == "ObjList");
            var arrayChildren = objListField is not null ? TryGetArrayChildren(objListField) : null;
            if (arrayChildren is not null)
            {
                var pathIds = new List<long>();
                foreach (var entry in arrayChildren.Children)
                {
                    var pptr = UnwrapArrayElement(entry);
                    if (pptr is not null && TryGetPPtrIds(pptr, out _, out var pathId))
                    {
                        pathIds.Add(pathId);
                    }
                }

                _spine.SpineOrderList = pathIds;
                _spine.FoundSize = new SizeI(1000, 1080);
            }
        }
        else if (className == "XEffectScaler")
        {
            var width = obj.Children.FirstOrDefault(c => c.FieldName == "DesignWidth")?.AsFloat;
            var height = obj.Children.FirstOrDefault(c => c.FieldName == "DesignHeight")?.AsFloat;
            if (width is not null && height is not null)
            {
                _spine.FoundSize = new SizeI((int)Math.Round(width.Value), (int)Math.Round(height.Value));
            }
        }
    }

    private SpineInfo? HandleSkeleton(
        AssetTypeValueField skeletonObject, AssetFileInfo skeletonInfo, AssetsFileInstance file)
    {
        var innerName = skeletonObject.Children.FirstOrDefault(c => c.FieldName == "m_Name")?.AsString ?? "";
        var spine = new SpineInfo("<unknown>", innerName);
        spine.Ids.Add(skeletonInfo.PathId);

        var componentField = skeletonObject.Children.FirstOrDefault(c => c.FieldName == "m_Component");
        var componentEntries = componentField is not null ? TryGetArrayChildren(componentField) : null;

        foreach (var entry in componentEntries?.Children ?? [])
        {
            var pptr = UnwrapArrayElement(entry);
            if (pptr is null)
            {
                continue;
            }

            var resolved = TryResolvePPtr(pptr, file);
            if (resolved is null)
            {
                continue;
            }

            var (obj, info, objFile) = resolved.Value;
            spine.Ids.Add(info.PathId);

            if (info.TypeId == (int)AssetClassID.RectTransform)
            {
                ApplyRectTransform(spine, obj, info, objFile);
            }
            else
            {
                var sortOrder = obj.Children.FirstOrDefault(c => c.FieldName == "m_SortingOrder")?.AsInt;
                if (sortOrder is not null)
                {
                    spine.Order = sortOrder.Value;
                }
            }

            if (info.TypeId != (int)AssetClassID.MonoBehaviour)
            {
                continue;
            }

            var scriptType = GetMonoScriptClassName(obj, objFile);
            var anim = obj.Children.FirstOrDefault(c => c.FieldName is "_animationName" or "animationName")?.AsString;
            if (!string.IsNullOrEmpty(anim))
            {
                spine.DefaultAnimation = anim;
            }

            var fieldNames = obj.Children.Select(c => c.FieldName).ToHashSet();
            if (fieldNames.Contains("skeletonDataAsset"))
            {
                ApplySkeletonData(spine, obj, objFile);
            }
            else if (scriptType == "XEffectScaler")
            {
                var width = obj.Children.FirstOrDefault(c => c.FieldName == "DesignWidth")?.AsFloat;
                var height = obj.Children.FirstOrDefault(c => c.FieldName == "DesignHeight")?.AsFloat;
                if (width is not null && height is not null)
                {
                    spine.Size = new SizeI((int)Math.Round(width.Value), (int)Math.Round(height.Value));
                }
            }
        }

        if (spine.Valid())
        {
            return spine;
        }

        Console.Error.WriteLine($"Invalid spine: {spine}");
        return null;
    }

    private void ApplyRectTransform(
        SpineInfo spine, AssetTypeValueField rectTransform, AssetFileInfo info, AssetsFileInstance file)
    {
        var pos = ReadVector2(rectTransform, "m_LocalPosition");
        var scale = ReadVector2(rectTransform, "m_LocalScale");
        var pivot = ReadVector2(rectTransform, "m_Pivot");

        if (pos is not null)
        {
            spine.Position = pos.Value;
        }

        if (scale is not null)
        {
            spine.SetScale(scale.Value.X);
        }

        if (pivot is not null)
        {
            spine.Pivot = pivot.Value;
        }

        spine.TransformId = info.PathId;

        var fatherField = rectTransform.Children.FirstOrDefault(c => c.FieldName == "m_Father");
        if (fatherField is not null && TryGetPPtrIds(fatherField, out _, out var fatherPathId) && fatherPathId != 0)
        {
            var father = TryResolvePPtr(fatherField, file);
            if (father is not null && father.Value.Info.TypeId == (int)AssetClassID.RectTransform)
            {
                var fatherPos = ReadVector2(father.Value.Field, "m_LocalPosition");
                if (fatherPos is not null && pos is not null)
                {
                    spine.Position = new PointF(pos.Value.X + fatherPos.Value.X, pos.Value.Y + fatherPos.Value.Y);
                }
            }
        }
    }

    private static PointF? ReadVector2(AssetTypeValueField obj, string fieldName)
    {
        var field = obj.Children.FirstOrDefault(c => c.FieldName == fieldName);
        if (field is null)
        {
            return null;
        }

        var x = field.Children.FirstOrDefault(c => c.FieldName == "x")?.AsFloat;
        var y = field.Children.FirstOrDefault(c => c.FieldName == "y")?.AsFloat;
        return x is not null && y is not null ? new PointF(x.Value, y.Value) : null;
    }

    private void ApplySkeletonData(SpineInfo spine, AssetTypeValueField skeletonGraphic, AssetsFileInstance file)
    {
        var materialField = skeletonGraphic.Children.FirstOrDefault(c => c.FieldName == "m_Material");
        if (materialField is not null && TryGetPPtrIds(materialField, out _, out var matPathId) && matPathId != 0)
        {
            var material = TryResolvePPtr(materialField, file);
            if (material is not null)
            {
                var tex = TextureFromMaterial(material.Value.Field, material.Value.File);
                if (tex is not null)
                {
                    spine.Textures.Add(tex.Value);
                }
            }
        }

        var skeletonDataAssetField = skeletonGraphic.Children.FirstOrDefault(c => c.FieldName == "skeletonDataAsset");
        var skeletonDataAsset = skeletonDataAssetField is not null
            ? TryResolvePPtr(skeletonDataAssetField, file)
            : null;
        if (skeletonDataAsset is null)
        {
            return;
        }

        // From here on everything is resolved relative to the skeleton data
        // asset's OWN file - for a split spine that is a different bundle
        // than the prefab's.
        var sdaFile = skeletonDataAsset.Value.File;

        var skeletonJsonField = skeletonDataAsset.Value.Field.Children
            .FirstOrDefault(c => c.FieldName == "skeletonJSON");
        var skeletonJson = skeletonJsonField is not null ? TryResolvePPtr(skeletonJsonField, sdaFile) : null;
        if (skeletonJson is not null)
        {
            spine.Name = skeletonJson.Value.Field.Children.FirstOrDefault(c => c.FieldName == "m_Name")?.AsString
                ?? spine.Name;
            spine.Json = skeletonJson.Value.Field.Children.FirstOrDefault(c => c.FieldName == "m_Script")?.AsByteArray;
        }

        var atlasAssetsField = skeletonDataAsset.Value.Field.Children.FirstOrDefault(c => c.FieldName == "atlasAssets");
        var atlasAssetsArray = atlasAssetsField is not null ? TryGetArrayChildren(atlasAssetsField) : null;
        var firstAtlasEntry = atlasAssetsArray?.Children.FirstOrDefault();
        var atlasAsset = firstAtlasEntry is not null ? TryResolvePPtr(firstAtlasEntry, sdaFile) : null;
        if (atlasAsset is null)
        {
            return;
        }

        var atlasFileInst = atlasAsset.Value.File;

        var atlasFileField = atlasAsset.Value.Field.Children.FirstOrDefault(c => c.FieldName == "atlasFile");
        var atlasFile = atlasFileField is not null ? TryResolvePPtr(atlasFileField, atlasFileInst) : null;
        if (atlasFile is not null)
        {
            var scriptBytes = atlasFile.Value.Field.Children.FirstOrDefault(c => c.FieldName == "m_Script")?.AsByteArray;
            spine.Atlas = scriptBytes is not null ? System.Text.Encoding.UTF8.GetString(scriptBytes) : null;
        }

        var materialsField = atlasAsset.Value.Field.Children.FirstOrDefault(c => c.FieldName == "materials");
        var materialsArray = materialsField is not null ? TryGetArrayChildren(materialsField) : null;
        foreach (var matEntry in materialsArray?.Children ?? [])
        {
            var mat = TryResolvePPtr(matEntry, atlasFileInst);
            if (mat is null)
            {
                continue;
            }

            var tex = TextureFromMaterial(mat.Value.Field, mat.Value.File);
            if (tex is not null)
            {
                spine.Textures.Add(tex.Value);
            }
        }
    }

    private (string Name, byte[] RgbaPixels, int Width, int Height)? TextureFromMaterial(
        AssetTypeValueField material, AssetsFileInstance file)
    {
        var savedPropsField = material.Children.FirstOrDefault(c => c.FieldName == "m_SavedProperties");
        var texEnvsField = savedPropsField?.Children.FirstOrDefault(c => c.FieldName == "m_TexEnvs");
        var texEnvsArray = texEnvsField is not null ? TryGetArrayChildren(texEnvsField) : null;

        foreach (var entry in texEnvsArray?.Children ?? [])
        {
            var name = entry.Children.FirstOrDefault(c => c.FieldName == "first")?.AsString;
            if (name != "_MainTex")
            {
                continue;
            }

            var second = TryGetPairSecond(entry);
            var textureField = second?.Children.FirstOrDefault(c => c.FieldName == "m_Texture");
            if (textureField is null)
            {
                return null;
            }

            var texture = TryResolvePPtr(textureField, file);
            if (texture is null)
            {
                return null;
            }

            var textureName = texture.Value.Field.Children
                .FirstOrDefault(c => c.FieldName == "m_Name")?.AsString ?? "texture";

            // Streamed pixel data lives in the .resS beside the texture's own
            // assets file, which is not necessarily the file we started from.
            var siblingEntries = _siblingEntriesByFile.GetValueOrDefault(texture.Value.File.name)
                ?? new Dictionary<string, byte[]>();

            var decoded = TextureDecoding.DecodeTexture2D(
                _am, texture.Value.File, texture.Value.Info, siblingEntries);
            return decoded is null ? null : (textureName, decoded.Value.Pixels, decoded.Value.Width, decoded.Value.Height);
        }

        return null;
    }

    private BoneFollower? HandleBoneFollower(AssetTypeValueField obj, AssetsFileInstance file)
    {
        var boneName = obj.Children.FirstOrDefault(c => c.FieldName == "boneName")?.AsString;
        var skeletonGraphicField = obj.Children.FirstOrDefault(c => c.FieldName == "skeletonGraphic");
        var skeletonGraphic = skeletonGraphicField is not null ? TryResolvePPtr(skeletonGraphicField, file) : null;
        if (boneName is null || skeletonGraphic is null)
        {
            return null;
        }

        var skeletonDataAssetField = skeletonGraphic.Value.Field.Children
            .FirstOrDefault(c => c.FieldName == "skeletonDataAsset");
        var skeletonDataAsset = skeletonDataAssetField is not null
            ? TryResolvePPtr(skeletonDataAssetField, skeletonGraphic.Value.File)
            : null;
        var skeletonJsonField = skeletonDataAsset?.Field.Children.FirstOrDefault(c => c.FieldName == "skeletonJSON");
        var skeletonJson = skeletonJsonField is not null && skeletonDataAsset is not null
            ? TryResolvePPtr(skeletonJsonField, skeletonDataAsset.Value.File)
            : null;
        var skeletonName = skeletonJson?.Field.Children.FirstOrDefault(c => c.FieldName == "m_Name")?.AsString;

        if (skeletonName is null)
        {
            return null;
        }

        var follower = new BoneFollower(boneName, skeletonName);

        var gameObjectField = obj.Children.FirstOrDefault(c => c.FieldName == "m_GameObject");
        if (gameObjectField is not null)
        {
            foreach (var (node, info, _) in WalkObjectChildren(gameObjectField, file))
            {
                if (info.TypeId == (int)AssetClassID.RectTransform)
                {
                    follower.Transforms.Add(info.PathId);
                }
            }
        }

        return follower;
    }

    /// <summary>
    /// BFS over an object's descendant hierarchy (m_Children/m_Component
    /// only - not a general graph walk).
    /// </summary>
    private IEnumerable<(AssetTypeValueField Field, AssetFileInfo Info, AssetsFileInstance File)> WalkObjectChildren(
        AssetTypeValueField rootPptr, AssetsFileInstance rootFile)
    {
        var seen = new HashSet<long>();
        var queue = new Queue<(AssetTypeValueField Field, AssetsFileInstance File)>();
        queue.Enqueue((rootPptr, rootFile));

        while (queue.Count > 0)
        {
            var (current, currentFile) = queue.Dequeue();

            // Queue items are either a direct PPtr field (the initial root,
            // or a bare m_Children entry) or an array element that still
            // needs unwrapping (an m_Component entry) - handle both.
            var pptrField = TryGetPPtrIds(current, out _, out _) ? current : UnwrapArrayElement(current);
            if (pptrField is null || !TryGetPPtrIds(pptrField, out _, out var pathId) || pathId == 0)
            {
                continue;
            }

            if (!seen.Add(pathId))
            {
                continue;
            }

            var resolved = TryResolvePPtr(pptrField, currentFile);
            if (resolved is null)
            {
                continue;
            }

            var (node, info, nodeFile) = resolved.Value;
            yield return (node, info, nodeFile);

            var childrenField = node.Children.FirstOrDefault(c => c.FieldName == "m_Children");
            var childrenArray = childrenField is not null ? TryGetArrayChildren(childrenField) : null;
            foreach (var child in childrenArray?.Children ?? [])
            {
                queue.Enqueue((child, nodeFile));
            }

            var componentField = node.Children.FirstOrDefault(c => c.FieldName == "m_Component");
            var componentArray = componentField is not null ? TryGetArrayChildren(componentField) : null;
            foreach (var component in componentArray?.Children ?? [])
            {
                queue.Enqueue((component, nodeFile));
            }
        }
    }

    private (double? Scale, HashSet<long>? PathIds) CheckGlobalScale(
        AssetFileInfo lastInfo, AssetsFileInstance file)
    {
        if (lastInfo.TypeId != (int)AssetClassID.GameObject)
        {
            return (null, null);
        }

        AssetTypeValueField obj;
        try
        {
            obj = _am.GetBaseField(file, lastInfo);
        }
        catch
        {
            return (null, null);
        }

        var componentField = obj.Children.FirstOrDefault(c => c.FieldName == "m_Component");
        var componentArray = componentField is not null ? TryGetArrayChildren(componentField) : null;

        foreach (var entry in componentArray?.Children ?? [])
        {
            var pptr = UnwrapArrayElement(entry);
            if (pptr is null)
            {
                continue;
            }

            var resolved = TryResolvePPtr(pptr, file);
            if (resolved is null)
            {
                continue;
            }

            var (c, info, cFile) = resolved.Value;
            if (info.TypeId != (int)AssetClassID.RectTransform && info.TypeId != (int)AssetClassID.Transform)
            {
                continue;
            }

            var localScale = ReadVector2(c, "m_LocalScale");
            if (localScale is null)
            {
                continue;
            }

            var scale = localScale.Value.X;
            var pathIds = new HashSet<long> { info.PathId };

            var childrenField = c.Children.FirstOrDefault(cf => cf.FieldName == "m_Children");
            var childrenArray = childrenField is not null ? TryGetArrayChildren(childrenField) : null;
            var children = childrenArray?.Children.ToList() ?? [];

            if (children.Count == 1)
            {
                var childPptrField = UnwrapArrayElement(children[0]);
                if (childPptrField is not null)
                {
                    var child = TryResolvePPtr(childPptrField, cFile);
                    if (child is not null && child.Value.Info.TypeId == (int)AssetClassID.RectTransform)
                    {
                        pathIds.Add(child.Value.Info.PathId);
                        var childScale = ReadVector2(child.Value.Field, "m_LocalScale");
                        if (childScale is not null)
                        {
                            var extraScale = childScale.Value.X;
                            if (extraScale > 2)
                            {
                                extraScale /= 100;
                            }

                            scale *= extraScale;
                        }
                    }
                }
            }

            return (scale, pathIds);
        }

        return (null, null);
    }
}
