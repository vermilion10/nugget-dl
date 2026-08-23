using System.Text;
using NuggetDl.Core.BinaryTable;
using NuggetDl.Core.Sources;

namespace NuggetDl.Core.Extraction;

/// <summary>
/// Rewrites a TextAsset's raw bytes and destination path before writing to
/// disk: converts binary-table payloads to CSV, decrypts obfuscated .lua
/// scripts, strips a leading RSA-signature block from non-UTF8 payloads, and
/// drops the trailing ".bytes" extension.
/// </summary>
public static class TextAssetRewriter
{
    private const string TempBytesMarker = "/temp/bytes/";

    private static byte[] XCryptoKey => ClientValues.TextKey;

    /// <summary>Byte-rotation XOR cipher used for obfuscated .lua scripts. Symmetric (its own inverse).</summary>
    public static byte[] Decrypt(byte[] content)
    {
        var result = (byte[])content.Clone();
        var count = result.Length;
        var num = count % XCryptoKey.Length;

        for (var i = count - 1; i >= 0; i--)
        {
            var num2 = i; // offset is always 0 for our callers
            var num3 = result[num2];
            var num4 = ((i + 1 < count ? result[i + 1] : 0) + count) % 8;
            num3 = (byte)((num3 >> (8 - num4)) | (num3 << num4));
            num3 ^= XCryptoKey[i % XCryptoKey.Length];

            if (num2 > 0)
            {
                num3 ^= result[num2 - 1];
            }

            num3 ^= XCryptoKey[num];
            result[num2] = num3;
        }

        return result;
    }

    private static bool IsUtf8(byte[] data)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The binary-table parser is lenient: blobs under /temp/bytes/ that
    /// aren't tables at all (navmesh, colliders, path areas) "parse" into
    /// nameless, column-less junk instead of raising. Real tables always
    /// have columns with stable, printable-ASCII names - so require that
    /// rather than trusting a successful parse alone.
    /// </summary>
    private static bool LooksLikeBinaryTable(IReadOnlyList<Column> columns)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        foreach (var column in columns)
        {
            if (string.IsNullOrEmpty(column.Name) || !column.Name.All(c => c < 128 && !char.IsControl(c)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Convert a binary-table payload to CSV, or return null if it isn't one.</summary>
    private static byte[]? TryConvertToCsv(byte[] data, Version gameVersion)
    {
        BinaryTable.BinaryTable table;

        try
        {
            table = new BinaryTable.BinaryTable(data, gameVersion);
        }
        catch (Exception e) when (
            e is BinaryTableException or ArgumentOutOfRangeException or IndexOutOfRangeException
            or FormatException or OverflowException)
        {
            return null;
        }

        if (!LooksLikeBinaryTable(table.Columns))
        {
            return null;
        }

        using var output = new StringWriter();
        table.ToCsv(output);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>Tables come as both foo.tab.bytes and foo.bytes.</summary>
    private static string BinaryTableCsvPath(string path)
    {
        foreach (var suffix in new[] { ".tab.bytes", ".bytes" })
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                return path[..^suffix.Length] + ".csv";
            }
        }

        // Rare path with no .bytes suffix (historically a directory).
        var ext = Path.GetExtension(path);
        var basePath = path[..^ext.Length];
        return $"{basePath}/{Path.GetFileName(basePath)}.csv";
    }

    public sealed record Rewritten(string Path, byte[] Data);

    public static Rewritten Rewrite(
        string path, byte[] data, Version gameVersion, bool allowBinaryTableConvert = false)
    {
        bool? dataIsUtf8 = null;
        var normalizedPath = path.Replace('\\', '/');

        if (normalizedPath.Contains(TempBytesMarker))
        {
            var csv = allowBinaryTableConvert ? TryConvertToCsv(data, gameVersion) : null;
            if (csv is not null)
            {
                data = csv;
                path = BinaryTableCsvPath(path);
            }
        }
        else if (data.Length > 128)
        {
            dataIsUtf8 = IsUtf8(data);
            if (!dataIsUtf8.Value)
            {
                // Leading RSA signature block - drop it.
                data = data[128..];
                dataIsUtf8 = null;
            }
        }

        // Almost everything ends in .bytes; anything else is left alone.
        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".bytes", StringComparison.Ordinal))
        {
            return new Rewritten(path, data);
        }

        var pathWithoutBytes = path[..^ext.Length];
        var innerExt = Path.GetExtension(pathWithoutBytes);

        if (string.Equals(innerExt, ".lua", StringComparison.Ordinal))
        {
            dataIsUtf8 ??= IsUtf8(data);
            if (!dataIsUtf8.Value)
            {
                return new Rewritten(pathWithoutBytes, Decrypt(data));
            }
        }

        return new Rewritten(pathWithoutBytes, data);
    }
}
