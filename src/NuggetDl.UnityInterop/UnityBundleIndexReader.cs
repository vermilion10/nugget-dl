using AssetsTools.NET;
using AssetsTools.NET.Extra;
using NuggetDl.Core.Sources;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Implements IBundleIndexReader using AssetsTools.NET, with a manual
/// UnityCN decryption path (see EncryptedBundleReader/UnityCnDecryptor) for
/// bundles AssetsTools.NET can't parse on its own since it has no
/// encryption support at all.
///
/// Streams here are deliberately never disposed: every one is a MemoryStream
/// over an already-in-memory byte[], which holds no OS resources, so
/// skipping Dispose is simpler and safer than trying to keep a `using` scope
/// alive for exactly as long as AssetsTools.NET's lazy field reads need it -
/// getting that lifetime wrong was an earlier bug in this file.
/// </summary>
public sealed class UnityBundleIndexReader : IBundleIndexReader
{
    private byte[]? _decryptKeyBytes;

    public void SetDecryptKey(string? key)
    {
        _decryptKeyBytes = string.IsNullOrEmpty(key) ? null : System.Text.Encoding.UTF8.GetBytes(key);
    }

    public byte[]? ReadTextAsset(byte[] bundleBytes, IReadOnlyList<string> candidatePaths)
    {
        var (am, afileInst, containerMap) = LoadBundleContainer(bundleBytes);

        foreach (var candidate in candidatePaths)
        {
            if (!containerMap.TryGetValue(candidate, out var info))
            {
                continue;
            }

            var baseField = am.GetBaseField(afileInst, info);
            // TextAsset's m_Script deserializes as a UTF-8-ish string field in
            // AssetsTools.NET's typetree; get its raw bytes rather than the
            // decoded string, to avoid a lossy round trip
            return baseField["m_Script"].AsByteArray;
        }

        return null;
    }

    public byte[]? ReadTextAssetByBasename(byte[] bundleBytes, string basename)
    {
        var (am, afileInst, containerMap) = LoadBundleContainer(bundleBytes);

        var match = containerMap.Keys.FirstOrDefault(path => path.Split('/')[^1] == basename);
        if (match is null)
        {
            return null;
        }

        var baseField = am.GetBaseField(afileInst, containerMap[match]);
        return baseField["m_Script"].AsByteArray;
    }

    /// <summary>
    /// Loads a bundle (encrypted or not) and its container map. Encrypted
    /// bundles are fully parsed and decrypted by hand via
    /// EncryptedBundleReader first, then directory entry 0's already-decrypted
    /// bytes are handed to AssetsTools.NET as a plain serialized file (which
    /// has the identical internal structure - AssetBundle object with
    /// m_Container, TextAssets, etc. - as the non-encrypted case, since
    /// encryption only wraps the container format, not what's inside it).
    /// </summary>
    private (AssetsManager Manager, AssetsFileInstance AssetsFileInstance, Dictionary<string, AssetFileInfo> ContainerMap)
        LoadBundleContainer(byte[] bundleBytes)
    {
        var am = new AssetsManager();
        AssetsFileInstance afileInst;

        if (EncryptedBundleReader.IsEncryptedUnityFsBundle(bundleBytes))
        {
            if (_decryptKeyBytes is null)
            {
                throw new InvalidOperationException(
                    "Bundle is UnityCN-encrypted but no decrypt key has been set "
                    + "(call IBundleIndexReader.SetDecryptKey first).");
            }

            var entries = EncryptedBundleReader.Read(bundleBytes, _decryptKeyBytes);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("Encrypted bundle had no directory entries after decryption");
            }

            var entry = entries[0];
            var entryStream = new MemoryStream(entry.Data);
            afileInst = am.LoadAssetsFile(entryStream, entry.Path, true);
        }
        else
        {
            var stream = new MemoryStream(bundleBytes);
            var bundleInst = am.LoadBundleFile(stream, "bundle", true);
            afileInst = am.LoadAssetsFileFromBundle(bundleInst, 0, true);
        }

        var containerMap = BuildContainerPathMap(am, afileInst);
        return (am, afileInst, containerMap);
    }

    public byte[]? ReadObjectRawDataByName(byte[] assetFileBytes, string objectName)
    {
        var am = new AssetsManager();
        var stream = new MemoryStream(assetFileBytes);

        // resources.assets is a bare serialized assets file, not a bundle -
        // never UnityCN-encrypted, so no decryption needed here.
        var afileInst = am.LoadAssetsFile(stream, "resources.assets", true);
        var afile = afileInst.file;

        // GetBaseField/typetree deserialization can't be trusted here: a
        // MonoBehaviour whose script type isn't registered in the typetree
        // (which XBuildConfig's isn't) either throws or comes back with no
        // usable m_Name. So this reads raw bytes directly for every
        // MonoBehaviour and peeks the name off the fixed header layout -
        // m_GameObject PPtr (12 bytes) + m_Enabled (4 bytes) + m_Script PPtr
        // (12 bytes) = 28 bytes, then a length-prefixed UTF-8 name - which is
        // exactly what XBuildConfig.Parse (Core) expects to walk from byte 0
        // of whatever this returns.
        foreach (var info in afile.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var raw = ReadRawObjectBytes(afile, info);
            if (RawMonoBehaviourNameMatches(raw, objectName))
            {
                return raw;
            }
        }

        return null;
    }

    private const int MonoBehaviourNameOffset = 28; // m_GameObject(12) + m_Enabled(4) + m_Script(12)

    private static bool RawMonoBehaviourNameMatches(byte[] raw, string objectName)
    {
        if (raw.Length < MonoBehaviourNameOffset + 4)
        {
            return false;
        }

        var nameLength = BitConverter.ToInt32(raw, MonoBehaviourNameOffset);
        if (nameLength < 0 || nameLength > 4096)
        {
            return false;
        }

        var nameStart = MonoBehaviourNameOffset + 4;
        if (nameStart + nameLength > raw.Length)
        {
            return false;
        }

        var name = System.Text.Encoding.UTF8.GetString(raw, nameStart, nameLength);
        return name == objectName;
    }

    /// <summary>
    /// Reads an object's raw serialized bytes directly, bypassing typetree
    /// deserialization. Needed for XBuildConfig since its MonoBehaviour script
    /// isn't in the typetree.
    /// </summary>
    private static byte[] ReadRawObjectBytes(AssetsFile afile, AssetFileInfo info)
    {
        var reader = afile.Reader;
        reader.Position = info.GetAbsoluteByteOffset(afile);
        return reader.ReadBytes((int)info.ByteSize);
    }

    /// <summary>
    /// Reads the bundle's AssetBundle object (m_Container) to map each
    /// container path (e.g. "assets/temp/index.bytes") to the AssetFileInfo
    /// it points to. Mirrors UnityPy's env.container.
    /// </summary>
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
