using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using SevenZip.Compression.LZMA;

namespace NuggetDl.UnityInterop;

/// <summary>
/// Manually parses a UnityFS bundle, decrypting it with
/// <see cref="UnityCnDecryptor"/> when the UsesAssetBundleEncryption flag is
/// set, and returns each directory entry's fully decompressed bytes.
///
/// This exists because AssetsTools.NET has no UnityCN support at all (a full
/// source search turned up nothing), so encrypted bundles must be parsed by
/// hand up through decompression, then handed to AssetsTools.NET as an
/// already-decrypted, already-decompressed serialized file. Structure and
/// field order are ported from UnityPy's BundleFile.read_fs
/// reusing AssetsTools.NET's own decompression primitives (SevenZipHelper,
/// Lz4DecoderStream) rather than reimplementing LZMA/LZ4.
///
/// Only the UnityFS container format is supported (not the older UnityWeb/
/// UnityRaw formats) - that's the only one modern builds use.
/// </summary>
public static class EncryptedBundleReader
{
    private static readonly Regex EngineVersionPattern = new(@"^(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private const uint CompressionTypeMask = 0x3F;
    private const uint BlocksInfoAtTheEndFlag = 0x80;
    private const uint BlockInfoNeedPaddingAtStartFlagNew = 0x200; // ArchiveFlags (new-layout builds)
    private const uint UsesAssetBundleEncryptionFlagOld = 0x200;   // ArchiveFlagsOld
    private const uint UsesAssetBundleEncryptionFlagNew = 0x1400;  // ArchiveFlags
    private const uint PerBlockEncryptedFlag = 0x100;

    public sealed record DirectoryEntry(string Path, byte[] Data);

    /// <summary>
    /// Peeks the bundle header (without consuming <paramref name="stream"/>
    /// past what's needed) to check whether it's UnityFS and whether the
    /// encryption flag is set, so the caller can decide whether to use this
    /// reader at all or fall back to AssetsTools.NET's normal bundle loading.
    /// </summary>
    public static bool IsEncryptedUnityFsBundle(byte[] bundleBytes)
    {
        using var stream = new MemoryStream(bundleBytes);
        var reader = new AssetsFileReader(stream) { BigEndian = true };

        var signature = reader.ReadNullTerminated();
        if (signature != "UnityFS")
        {
            return false;
        }

        var formatVersion = reader.ReadUInt32();
        _ = reader.ReadNullTerminated(); // versionPlayer
        var versionEngine = reader.ReadNullTerminated();

        _ = reader.ReadInt64();  // size
        _ = reader.ReadUInt32(); // compressedSize
        _ = reader.ReadUInt32(); // uncompressedSize
        var dataFlags = reader.ReadUInt32();

        var engineVersion = ParseEngineVersion(versionEngine);
        var encryptionFlag = UsesOldFlagLayout(formatVersion, engineVersion)
            ? UsesAssetBundleEncryptionFlagOld
            : UsesAssetBundleEncryptionFlagNew;

        return (dataFlags & encryptionFlag) != 0;
    }

    public static List<DirectoryEntry> Read(byte[] bundleBytes, byte[] decryptKeyBytes)
    {
        using var stream = new MemoryStream(bundleBytes);
        var reader = new AssetsFileReader(stream) { BigEndian = true };

        var signature = reader.ReadNullTerminated();
        if (signature != "UnityFS")
        {
            throw new NotSupportedException($"Only UnityFS bundles are supported, got '{signature}'");
        }

        var formatVersion = reader.ReadUInt32();
        _ = reader.ReadNullTerminated(); // versionPlayer
        var versionEngine = reader.ReadNullTerminated();

        _ = reader.ReadInt64();               // size (total bundle size, unused here)
        var compressedSize = reader.ReadUInt32();
        var uncompressedSize = reader.ReadUInt32();
        var dataFlags = reader.ReadUInt32();

        var engineVersion = ParseEngineVersion(versionEngine);
        var oldFlagLayout = UsesOldFlagLayout(formatVersion, engineVersion);
        var encryptionFlag = oldFlagLayout ? UsesAssetBundleEncryptionFlagOld : UsesAssetBundleEncryptionFlagNew;

        UnityCnDecryptor? decryptor = null;
        if ((dataFlags & encryptionFlag) != 0)
        {
            decryptor = UnityCnDecryptor.Read(reader, decryptKeyBytes);
        }

        // Header version >= 7, or 2019.4.15+ engines even at header version 6,
        // align to 16 bytes before the blocksInfo/data region begins.
        if (formatVersion >= 7 || (engineVersion is [2019, ..] && CompareVersion(engineVersion, [2019, 4, 15]) >= 0))
        {
            reader.Align16();
        }

        var regionStart = reader.Position;
        byte[] blocksInfoRaw;
        if ((dataFlags & BlocksInfoAtTheEndFlag) != 0)
        {
            reader.Position = stream.Length - compressedSize;
            blocksInfoRaw = reader.ReadBytes((int)compressedSize);
            reader.Position = regionStart;
        }
        else
        {
            blocksInfoRaw = reader.ReadBytes((int)compressedSize);
        }

        // blocksInfo itself is decrypted using the archive-level dataFlags as
        // the "flags" value (matching Python passing self.dataflags here) -
        // in practice this is almost never actually encrypted since 0x100
        // isn't normally set at the archive level, but the check is kept for
        // fidelity with the source rather than assumed away.
        var blocksInfoBytes = DecompressData(decryptor, blocksInfoRaw, uncompressedSize, dataFlags, blockIndex: 0);

        using var blocksInfoStream = new MemoryStream(blocksInfoBytes);
        var blocksInfoReader = new AssetsFileReader(blocksInfoStream) { BigEndian = true };

        _ = blocksInfoReader.ReadBytes(16); // uncompressedDataHash, unused

        var blockCount = blocksInfoReader.ReadInt32();
        var blocks = new (uint UncompressedSize, uint CompressedSize, ushort Flags)[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            blocks[i] = (blocksInfoReader.ReadUInt32(), blocksInfoReader.ReadUInt32(), blocksInfoReader.ReadUInt16());
        }

        var directoryCount = blocksInfoReader.ReadInt32();
        var directories = new (long Offset, long Size, uint Flags, string Path)[directoryCount];
        for (var i = 0; i < directoryCount; i++)
        {
            directories[i] = (
                blocksInfoReader.ReadInt64(),
                blocksInfoReader.ReadInt64(),
                blocksInfoReader.ReadUInt32(),
                blocksInfoReader.ReadNullTerminated());
        }

        if (!oldFlagLayout && (dataFlags & BlockInfoNeedPaddingAtStartFlagNew) != 0)
        {
            reader.Align16();
        }

        using var concatenated = new MemoryStream();
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            var compressedBlockBytes = reader.ReadBytes((int)block.CompressedSize);
            var decompressedBlockBytes = DecompressData(
                decryptor, compressedBlockBytes, block.UncompressedSize, block.Flags, blockIndex: i);
            concatenated.Write(decompressedBlockBytes, 0, decompressedBlockBytes.Length);
        }

        var allBytes = concatenated.ToArray();

        var entries = new List<DirectoryEntry>(directories.Length);
        foreach (var dir in directories)
        {
            var slice = new byte[dir.Size];
            Array.Copy(allBytes, dir.Offset, slice, 0, dir.Size);
            entries.Add(new DirectoryEntry(dir.Path, slice));
        }

        return entries;
    }

    private static byte[] DecompressData(
        UnityCnDecryptor? decryptor, byte[] compressedBytes, long uncompressedSize, uint flags, int blockIndex)
    {
        var compressionType = flags & CompressionTypeMask;

        if (decryptor is not null && (flags & PerBlockEncryptedFlag) != 0 && compressionType != 0)
        {
            decryptor.DecryptBlock(compressedBytes, blockIndex);
        }

        switch (compressionType)
        {
            case 0: // none
                return compressedBytes;

            case 1: // LZMA
            {
                using var input = new MemoryStream(compressedBytes);
                using var output = new MemoryStream();
                SevenZipHelper.StreamDecompress(input, output, compressedBytes.Length, uncompressedSize);
                return output.ToArray();
            }

            case 2: // LZ4
            case 3: // LZ4HC (decodes the same as LZ4)
            {
                using var input = new MemoryStream(compressedBytes);
                using var output = new MemoryStream();
                using var decoder = new AssetsTools.NET.Extra.Decompressors.LZ4.Lz4DecoderStream(input);
                decoder.CopyToCompat(output, uncompressedSize);
                return output.ToArray();
            }

            default:
                throw new NotSupportedException($"Unknown block compression type {compressionType}");
        }
    }

    /// <summary>
    /// Parses "2021.3.5f1" (etc.) into [major, minor, patch]. Returns [0, 0, 0]
    /// if it doesn't match (matching Python's behavior of falling through to
    /// the "old" flag layout when the version string is unparseable).
    /// </summary>
    private static int[] ParseEngineVersion(string versionEngine)
    {
        var match = EngineVersionPattern.Match(versionEngine);
        if (!match.Success)
        {
            return [0, 0, 0];
        }

        return
        [
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
        ];
    }

    private static bool UsesOldFlagLayout(uint formatVersion, int[] engineVersion)
    {
        // Unity CN encryption predates the on-disk alignment fix, and both
        // features reused the same flag bit on different Unity versions, so
        // the flag enum to use depends on which side of these thresholds
        // the engine version falls.
        if (CompareVersion(engineVersion, [2020]) < 0)
        {
            return true;
        }

        if (engineVersion[0] == 2020 && CompareVersion(engineVersion, [2020, 3, 34]) < 0)
        {
            return true;
        }

        if (engineVersion[0] == 2021 && CompareVersion(engineVersion, [2021, 3, 2]) < 0)
        {
            return true;
        }

        if (engineVersion[0] == 2022 && CompareVersion(engineVersion, [2022, 1, 1]) < 0)
        {
            return true;
        }

        return false;
    }

    private static int CompareVersion(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            var cmp = av.CompareTo(bv);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }
}
