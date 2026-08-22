using System.Text;
using System.Text.RegularExpressions;

namespace NuggetDl.Core.Sources;

public sealed class XBuildConfigException : Exception
{
    public XBuildConfigException(string message) : base(message)
    {
    }
}

/// <summary>
/// The per-build patch-CDN key and build metadata, extracted from the
/// XBuildConfig MonoBehaviour in resources.assets.
/// </summary>
public sealed partial record XBuildConfig(
    string BundleId,
    string DisplayVersion,
    string InternalVersion,
    string BuildNumber,
    string Key)
{
    [GeneratedRegex("^[A-Za-z0-9]{16}$")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Extracts the per-build patch-CDN key from the XBuildConfig
    /// MonoBehaviour's raw serialized bytes.
    ///
    /// XBuildConfig's script isn't in the typetree, so it can't be
    /// deserialized generically - the body follows Unity's standard
    /// MonoBehaviour layout (header + length-prefixed, 4-byte-aligned
    /// strings), which is walked by hand here.
    /// </summary>
    public static XBuildConfig Parse(ReadOnlySpan<byte> raw)
    {
        var reader = new AlignedBinaryReader(raw);
        reader.Skip(12); // m_GameObject
        reader.Skip(4);  // m_Enabled
        reader.Skip(12); // m_Script

        var name = reader.ReadAlignedString();
        if (name != "XBuildConfig")
        {
            throw new XBuildConfigException($"expected m_name 'XBuildConfig', got '{name}'");
        }

        var bundleId = reader.ReadAlignedString();
        var displayVersion = reader.ReadAlignedString();
        reader.ReadInt32(); // unknown int (0)
        var internalVersion = reader.ReadAlignedString();
        var buildNumber = reader.ReadAlignedString();
        reader.ReadInt32(); // unknown int (4)
        var key = reader.ReadAlignedString();

        if (!KeyPattern().IsMatch(key))
        {
            throw new XBuildConfigException($"implausible patch key '{key}'");
        }

        return new XBuildConfig(bundleId, displayVersion, internalVersion, buildNumber, key);
    }

    /// <summary>
    /// Loads resources.assets via <paramref name="reader"/> and returns the
    /// parsed XBuildConfig.
    /// </summary>
    public static XBuildConfig Extract(byte[] resourcesAssets, IBundleIndexReader reader)
    {
        var raw = reader.ReadObjectRawDataByName(resourcesAssets, "XBuildConfig")
            ?? throw new XBuildConfigException("XBuildConfig object not found in resources.assets");
        return Parse(raw);
    }

    /// <summary>Loads resources.assets and returns just the patch-CDN key.</summary>
    public static string ExtractBuildKey(byte[] resourcesAssets, IBundleIndexReader reader) =>
        Extract(resourcesAssets, reader).Key;
}

/// <summary>
/// Minimal little-endian reader for Unity's MonoBehaviour binary layout:
/// fixed-width ints and length-prefixed strings padded to 4-byte alignment.
/// </summary>
internal ref struct AlignedBinaryReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public void Skip(int count)
    {
        EnsureAvailable(count);
        _pos += count;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BitConverter.ToInt32(_data.Slice(_pos, 4));
        _pos += 4;
        return value;
    }

    public string ReadAlignedString()
    {
        var length = ReadInt32();
        if (length is < 0 or > 4096)
        {
            throw new XBuildConfigException($"implausible string length {length}");
        }

        EnsureAvailable(length);
        var text = Encoding.UTF8.GetString(_data.Slice(_pos, length));
        _pos += length;

        var padding = (4 - (length % 4)) % 4;
        Skip(padding);

        return text;
    }

    private readonly void EnsureAvailable(int count)
    {
        if (_pos + count > _data.Length)
        {
            throw new XBuildConfigException("unexpected end of data");
        }
    }
}
