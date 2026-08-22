using System.Text;

namespace NuggetDl.Core.BinaryTable;

/// <summary>
/// Low-level cursor reader for the binary table format's value types.
/// </summary>
public sealed class BinaryTableReader
{
    private const int MaxI32 = 2_147_483_647;
    private const int FloatToInt = 10_000;

    private readonly byte[] _buffer;
    private int _pos;
    private readonly bool _newFixnum;

    public bool UseStringPool { get; set; }
    public Func<int, string>? StringPoolCallback { get; set; }

    /// <param name="newFixnum">New-style fixnum encoding is from 3.3.0 (Jetavie) onward.</param>
    public BinaryTableReader(byte[] buffer, bool newFixnum)
    {
        _buffer = buffer;
        _pos = 0;
        _newFixnum = newFixnum;
    }

    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    public void Seek(int position) => _pos = position;

    public void SeekFromCurrent(int offset) => _pos += offset;

    public void SeekFromEnd(int offset) => _pos = _buffer.Length + offset;

    public void Move(int offset) => _pos += offset;

    public byte PeekByte() => _buffer[_pos];

    public byte[] ReadBytes(int size)
    {
        var start = _pos;
        _pos = start + size;
        return _buffer[start.._pos];
    }

    public byte ReadU8() => _buffer[_pos++];

    /// <summary>Little-endian signed 32-bit, built byte-by-byte so behavior
    /// doesn't depend on host endianness.</summary>
    public int ReadI32()
    {
        var b = ReadBytes(4);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    public long ReadLeb128()
    {
        long result = 0;
        var shift = 0;
        while (true)
        {
            var b = _buffer[_pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    public bool ReadBool() => ReadU8() == 1;

    public string ReadString(bool? forceUsePool = null)
    {
        if (forceUsePool == true || (forceUsePool is null && UseStringPool))
        {
            var index = ReadInt();
            return StringPoolCallback?.Invoke(index) ?? string.Empty;
        }

        var start = _pos;
        var end = Array.IndexOf(_buffer, (byte)0, start);
        if (end < 0)
        {
            throw new BinaryTableException("Unterminated string (no NUL terminator before end of buffer)");
        }

        _pos = end + 1;
        return Encoding.UTF8.GetString(_buffer, start, end - start);
    }

    /// <summary>Signed 32-bit int, bounds-checked from the unsigned LEB128 value
    /// the same way the original does - see the type's remarks for why this
    /// simplifies the bitwise formula without changing behavior for valid input.</summary>
    public int ReadInt()
    {
        var x = ReadLeb128();
        return x <= MaxI32 ? (int)x : (int)(x - 0x1_0000_0000L);
    }

    public double ReadFloat()
    {
        var x = ReadInt();
        return x == 0 ? 0.0 : x / (double)FloatToInt;
    }

    public List<string> ReadListString()
    {
        var count = ReadInt();
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadString());
        }

        return result;
    }

    public List<bool> ReadListBool()
    {
        var count = ReadInt();
        var result = new List<bool>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadBool());
        }

        return result;
    }

    public List<int> ReadListInt()
    {
        var count = ReadInt();
        var result = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadInt());
        }

        return result;
    }

    public List<double> ReadListFloat()
    {
        var count = ReadInt();
        var result = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadFloat());
        }

        return result;
    }

    public Dictionary<string, string> ReadDictStringString() => ReadDict(() => ReadString(), () => ReadString());

    public Dictionary<int, int> ReadDictIntInt() => ReadDict(() => ReadInt(), () => ReadInt());

    public Dictionary<int, string> ReadDictIntString() => ReadDict(() => ReadInt(), () => ReadString());

    public Dictionary<string, int> ReadDictStringInt() => ReadDict(() => ReadString(), () => ReadInt());

    public Dictionary<int, double> ReadDictIntFloat() => ReadDict(() => ReadInt(), () => ReadFloat());

    private Dictionary<TKey, TValue> ReadDict<TKey, TValue>(Func<TKey> readKey, Func<TValue> readValue)
        where TKey : notnull
    {
        var count = ReadInt();
        var d = new Dictionary<TKey, TValue>(count);
        for (var i = 0; i < count; i++)
        {
            var key = readKey();
            var value = readValue();
            d[key] = value;
        }

        return d;
    }

    /// <summary>
    /// A fixed-point decimal. Returns boxed `int` 0 for the empty/zero case
    /// (matching Python's literal `0`) or `decimal` otherwise - callers
    /// distinguish via type checks the same way the CSV writer's Python
    /// counterpart relies on str() working for either.
    /// </summary>
    public object ReadFix()
    {
        if (!_newFixnum)
        {
            // Old style: either empty (0x00) or a NUL-terminated decimal string.
            var n = ReadString();
            if (n.Length == 0)
            {
                return 0;
            }

            return decimal.Parse(n, System.Globalization.CultureInfo.InvariantCulture);
        }

        // New style: uLEB128 magnitude, then a sign+shift byte.
        var magnitude = ReadLeb128();
        if (magnitude == 0)
        {
            return 0;
        }

        var shiftByte = ReadBytes(1)[0];
        var flipSign = (shiftByte & 0x80) != 0;
        var shift = shiftByte & (0x80 - 1);

        decimal num = magnitude;
        for (var i = 0; i < shift; i++)
        {
            num /= 10m;
        }

        return flipSign ? -num : num;
    }

    public List<object> ReadListFix()
    {
        var count = ReadInt();
        var result = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadFix());
        }

        return result;
    }

    public List<object> ReadFix2() => [ReadFix(), ReadFix()];

    public List<object> ReadFix3() => [ReadFix(), ReadFix(), ReadFix()];

    public List<object> ReadFixQuaternion() => [ReadFix(), ReadFix(), ReadFix(), ReadFix()];

    public List<List<object>> ReadListFix2()
    {
        var count = ReadInt();
        var result = new List<List<object>>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadFix2());
        }

        return result;
    }

    public List<List<object>> ReadListFix3()
    {
        var count = ReadInt();
        var result = new List<List<object>>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadFix3());
        }

        return result;
    }

    public List<List<object>> ReadListFixQuaternion()
    {
        var count = ReadInt();
        var result = new List<List<object>>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadFixQuaternion());
        }

        return result;
    }

    public object ReadByColumnType(int type) => type switch
    {
        1 => ReadBool(),
        2 => ReadString(),
        3 => ReadFix(),
        4 => ReadListString(),
        5 => ReadListBool(),
        6 => ReadListInt(),
        7 => ReadListFloat(),
        8 => ReadListFix(),
        9 => ReadDictStringString(),
        10 => ReadDictIntInt(),
        11 => ReadDictIntString(),
        12 => ReadDictStringInt(),
        13 => ReadDictIntFloat(),
        14 => ReadInt(),
        15 => ReadFloat(),
        16 => ReadFix2(),
        17 => ReadFix3(),
        18 => ReadFixQuaternion(),
        19 => ReadListFix2(),
        20 => ReadListFix3(),
        21 => ReadListFixQuaternion(),
        _ => throw new BinaryTableException($"Unknown column type: {type}"),
    };
}
