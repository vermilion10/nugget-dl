using MessagePack;
using MessagePack.Resolvers;

namespace NuggetDl.Core.Sources;

/// <summary>
/// A single entry from the patch/game index: which blob a bundle lives in,
/// its sha1, and its size in bytes.
/// </summary>
public sealed record BundleIndexEntry(string Blob, string Sha1, long Size);

/// <summary>
/// Parses the msgpack'd index format shared by all sources.
/// </summary>
public static class IndexCodec
{
    /// <summary>
    /// Decodes a raw index payload (as extracted from the "index.bytes"
    /// TextAsset inside a bundle) into a flat bundle-name -&gt; entry map.
    ///
    /// The msgpack payload is a top-level array: slot 0 is the main index
    /// map, slot 1 (if present and a map) holds partial indices whose
    /// entries are merged in on top of the main index, later partials
    /// winning. Slot 2 has never been populated by the game and is ignored.
    /// </summary>
    // NOTE: verify this resolver choice once the package is restored - we need
    // "deserialize to plain object graph" (Dictionary<object,object>/object[]),
    // matching Python's msgpack.loads(strict_map_key=False). Contractless is the
    // resolver that supports untyped object deserialization; confirm the exact
    // static accessor against the installed MessagePack version (this shape has
    // moved around across major versions).
    private static readonly MessagePackSerializerOptions IndexOptions =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    public static Dictionary<string, BundleIndexEntry> ParseIndex(byte[] data)
    {
        var parsed = MessagePackSerializer.Deserialize<object>(data, IndexOptions);

        if (parsed is not object[] slots || slots.Length == 0)
        {
            throw new SourceIndexException("Index payload was not a top-level array");
        }

        var index = ToBundleMap(slots[0]);

        if (slots.Length > 1 && slots[1] is IDictionary<object, object> partials)
        {
            foreach (var partial in partials.Values)
            {
                foreach (var (bundle, entry) in ToBundleMap(partial))
                {
                    index[bundle] = entry;
                }
            }
        }

        return index;
    }

    private static Dictionary<string, BundleIndexEntry> ToBundleMap(object slot)
    {
        if (slot is not IDictionary<object, object> raw)
        {
            throw new SourceIndexException("Index slot was not a map");
        }

        var result = new Dictionary<string, BundleIndexEntry>(raw.Count);
        foreach (var (key, value) in raw)
        {
            var bundleName = ToStringKey(key);
            if (value is not object[] tuple || tuple.Length < 3)
            {
                throw new SourceIndexException(
                    $"Index entry for '{bundleName}' was not a 3-element tuple");
            }

            result[bundleName] = new BundleIndexEntry(
                Blob: ToStringKey(tuple[0]),
                Sha1: ToStringKey(tuple[1]),
                Size: Convert.ToInt64(tuple[2]));
        }

        return result;
    }

    private static string ToStringKey(object value) => value switch
    {
        string s => s,
        byte[] b => System.Text.Encoding.UTF8.GetString(b),
        _ => value.ToString() ?? string.Empty,
    };
}
