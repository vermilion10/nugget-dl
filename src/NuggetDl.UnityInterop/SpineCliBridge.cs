using AssetsTools.NET;
using AssetsTools.NET.Extra;
using NuggetDl.Core.Spine;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Loads a GROUP of bundles into one AssetsManager and drives SpineExtractor
/// over the prefabs they contain. CLI-facing glue, not part of the
/// IBundleContentReader abstraction since SpineExtractor needs direct
/// AssetsManager/AssetsFileInstance access that abstraction doesn't expose.
///
/// Why a group and not one bundle at a time: a spine prefab regularly points
/// at skeleton data living in a DIFFERENT bundle (confirmed 2026-08-22,
/// assets/product/ui/spine/2-17spine/qishitutorial.ab, whose two
/// SkeletonGraphics reference qishitutorial_bg.ab and qishitutorial_role.ab
/// through non-zero m_FileIDs). Those references only resolve when every
/// related file is loaded in the same manager, so extracting such a spine
/// one bundle at a time can never work - it produced two nameless, dataless
/// "Invalid spine" entries before this.
/// </summary>
public static class SpineCliBridge
{
    /// <summary>Container-path prefix every spine prefab sits under.</summary>
    public const string SpinePrefabPrefix = "assets/product/ui/spine/";

    private const string PrefabSuffix = ".prefab";

    /// <summary>One loaded assets file plus the sibling entries (.resS) that
    /// came out of its bundle.</summary>
    private sealed record LoadedFile(AssetsFileInstance Instance, IReadOnlyDictionary<string, byte[]> SiblingEntries);

    /// <summary>A spine prefab discovered in the loaded group: its
    /// Python-equivalent name (container path minus prefix/suffix) plus where
    /// the GameObject actually lives.</summary>
    private sealed record PrefabEntry(string Name, AssetsFileInstance File, AssetFileInfo Info);

    /// <summary>
    /// Extracts every spine prefab found across <paramref name="bundles"/>,
    /// which should be one "primary" bundle plus any related bundles its
    /// prefabs might reference.
    /// </summary>
    public static void ExtractGroup(
        IReadOnlyList<(string BundleName, byte[] Bytes)> bundles,
        byte[]? decryptKeyBytes,
        string outputDir,
        Version? gameVersion = null)
    {
        var am = new AssetsManager();
        var loaded = new List<LoadedFile>();

        foreach (var (bundleName, bytes) in bundles)
        {
            try
            {
                loaded.Add(LoadBundle(am, bundleName, bytes, decryptKeyBytes));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to load {bundleName}: {e.Message}");
            }
        }

        if (loaded.Count == 0)
        {
            return;
        }

        var files = loaded.Select(l => l.Instance).ToList();
        var siblingEntriesByFile = loaded.ToDictionary(
            l => l.Instance.name,
            l => l.SiblingEntries);

        var prefabs = FindPrefabs(am, loaded);
        if (prefabs.Count == 0)
        {
            Console.Error.WriteLine(
                $"No spine prefabs found in {string.Join(", ", bundles.Select(b => b.BundleName))}");
            return;
        }

        var byName = prefabs.ToDictionary(p => p.Name, p => p);

        foreach (var prefab in prefabs)
        {
            var name = prefab.Name;

            // spinelogin is versioned - the same prefab name is reused every
            // release, so its output is namespaced by major-minor.
            if (name == "spinelogin" && gameVersion is not null)
            {
                name += $"/{gameVersion.Major}-{gameVersion.Minor}";
            }

            if (SpineQuirks.ShouldSkip(name))
            {
                Console.Error.WriteLine($"Skipping {name} (absorbed into a glued spine)");
                continue;
            }

            if (SpineQuirks.FindGlue(name) is { } glue)
            {
                // Every layer must be present in the loaded group, otherwise
                // the glued result would silently be missing pieces.
                var missing = glue.Layers.Where(l => !byName.ContainsKey(l)).ToList();
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"Quirk glue {glue.Name}: missing layer(s) {string.Join(", ", missing)} - "
                        + "pass the bundles containing them too; extracting ungrouped instead");
                }
                else
                {
                    Console.Error.WriteLine($"Quirk triggered: glue {glue.Name}");
                    var last = byName[glue.Layers[^1]];
                    SpineExtractor.Extract(
                        glue.Name, files, siblingEntriesByFile, (last.File, last.Info), am, outputDir);
                    continue;
                }
            }

            SpineExtractor.Extract(
                name, files, siblingEntriesByFile, (prefab.File, prefab.Info), am, outputDir);
        }
    }

    private static LoadedFile LoadBundle(
        AssetsManager am, string bundleName, byte[] bundleBytes, byte[]? decryptKeyBytes)
    {
        if (EncryptedBundleReader.IsEncryptedUnityFsBundle(bundleBytes))
        {
            if (decryptKeyBytes is null)
            {
                throw new InvalidOperationException("Bundle is UnityCN-encrypted but no decrypt key was provided.");
            }

            var entries = EncryptedBundleReader.Read(bundleBytes, decryptKeyBytes);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("Encrypted bundle had no directory entries after decryption");
            }

            var byPath = new Dictionary<string, byte[]>();
            foreach (var e in entries)
            {
                byPath[e.Path] = e.Data;
            }

            // Loaded under the entry's own CAB name so that other files'
            // `archive:/CAB-x/CAB-x` externals resolve to it.
            var inst = am.LoadAssetsFile(new MemoryStream(entries[0].Data), entries[0].Path, true);
            return new LoadedFile(inst, byPath);
        }

        var bundleInst = am.LoadBundleFile(new MemoryStream(bundleBytes), bundleName, true);
        var plainInst = am.LoadAssetsFileFromBundle(bundleInst, 0, true);
        return new LoadedFile(plainInst, new Dictionary<string, byte[]>());
    }

    /// <summary>
    /// Collects every GameObject container entry ending in .prefab across the
    /// loaded files, naming each the way commands/spines.py does: container
    /// path minus the spine prefix and the .prefab suffix. That name (e.g.
    /// "sailika/sailika", not the bundle's filename "sailika") is what the
    /// quirk patterns are written against.
    /// </summary>
    private static List<PrefabEntry> FindPrefabs(AssetsManager am, IReadOnlyList<LoadedFile> loaded)
    {
        var prefabs = new List<PrefabEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in loaded)
        {
            var afile = file.Instance.file;
            var bundleInfo = afile.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault();
            if (bundleInfo is null)
            {
                continue;
            }

            AssetTypeValueField container;
            try
            {
                container = am.GetBaseField(file.Instance, bundleInfo)["m_Container.Array"];
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to read container of {file.Instance.name}: {e.Message}");
                continue;
            }

            foreach (var entry in container.Children)
            {
                var path = entry[0].AsString;
                if (!path.EndsWith(PrefabSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pathId = entry[1]["asset.m_PathID"].AsLong;
                var info = afile.GetAssetInfo(pathId);

                // The same .prefab path is listed several times, once per
                // object it produced (GameObject + its Meshes) - only the
                // GameObject is the prefab root.
                if (info is null || info.TypeId != (int)AssetClassID.GameObject)
                {
                    continue;
                }

                var name = path[..^PrefabSuffix.Length];
                if (name.StartsWith(SpinePrefabPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[SpinePrefabPrefix.Length..];
                }

                if (seen.Add(name))
                {
                    prefabs.Add(new PrefabEntry(name, file.Instance, info));
                }
            }
        }

        return prefabs;
    }
}
