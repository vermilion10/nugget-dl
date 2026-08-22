using System.Text;

namespace NuggetDl.Core.BinaryTable;

public sealed class BinaryTable
{
    private enum ColumnKind
    {
        Scalar,
        List,
        IntDict,
        StrDict,
    }

    private readonly BinaryTableReader _reader;

    private int _infoLength;
    private List<Column> _columns = [];

    /// <summary>Parsed columns, in file order. Exposed for callers that need to
    /// sanity-check a parse before trusting it.</summary>
    public IReadOnlyList<Column> Columns => _columns;

    private bool _hasPrimaryKey;
    private string _primaryKey = string.Empty;
    private int _primaryKeyLength;

    private int _rowTrunkLength;
    private int _rowCount;
    private int _contentTrunkLength;

    private readonly bool _enableStringPool;
    private int _poolColumnSize;
    private Dictionary<int, bool> _columnMap = [];
    private List<int> _poolOffsetInfoArray = [];
    private int _poolContentStartPos;

    private List<List<object>> _rows = [];

    private bool[] _poolFlags = [];
    private ColumnKind[] _columnKinds = [];

    /// <param name="gameVersion">Major.Minor is enough - new-style fixnum kicks
    /// in at 3.3+, the string pool at 3.6+.</param>
    public BinaryTable(byte[] data, Version gameVersion)
    {
        _reader = new BinaryTableReader(data, newFixnum: gameVersion >= new Version(3, 3));
        _enableStringPool = gameVersion >= new Version(3, 6);

        if (_enableStringPool)
        {
            _poolColumnSize = -1;
            _reader.StringPoolCallback = ReadPoolStringByIndex;
        }

        ReadHeader();
        if (_enableStringPool)
        {
            ReadStringPoolInfo();
        }

        PrecomputeColumnInfo();
        ReadContent();
    }

    private void PrecomputeColumnInfo()
    {
        _poolFlags = new bool[_columns.Count];
        _columnKinds = new ColumnKind[_columns.Count];
        for (var j = 0; j < _columns.Count; j++)
        {
            _poolFlags[j] = IsStringPoolColumn(j + 1);
            _columnKinds[j] = ColumnKindOf(_columns[j]);
        }
    }

    private static ColumnKind ColumnKindOf(Column column)
    {
        if (column.IsIntKeyedDict())
        {
            return ColumnKind.IntDict;
        }

        if (column.IsDictType())
        {
            return ColumnKind.StrDict;
        }

        // Lists (4-8), fixed-arity vectors (16-18) and vector lists (19-21) all
        // surface as list-shaped values whose max length drives indexed columns.
        if (column.Type is >= 4 and <= 8 or >= 16 and <= 21)
        {
            return ColumnKind.List;
        }

        return ColumnKind.Scalar;
    }

    private void ReadHeader()
    {
        _infoLength = _reader.ReadI32();
        _columns = [];

        var columnCount = _reader.ReadInt();
        for (var i = 0; i < columnCount; i++)
        {
            var columnType = _reader.ReadInt();
            var columnName = _reader.ReadString();
            _columns.Add(new Column(columnName, columnType));
        }

        _hasPrimaryKey = _reader.ReadBool();
        _primaryKey = string.Empty;
        _primaryKeyLength = 0;
        if (_hasPrimaryKey)
        {
            if (_enableStringPool)
            {
                var primaryKeyIndex = _reader.ReadInt();
                _primaryKey = _columns[primaryKeyIndex].Name;
            }
            else
            {
                _primaryKey = _reader.ReadString();
            }

            _primaryKeyLength = _reader.ReadInt();
        }

        _rowTrunkLength = _reader.ReadInt();
        _rowCount = _reader.ReadInt();
        _contentTrunkLength = _reader.ReadInt();
    }

    private int GetPoolOffsetTrunkStartPosition() =>
        4 + _infoLength + _primaryKeyLength + _rowTrunkLength + _contentTrunkLength;

    private int GetPoolContentTrunkStartPosition() =>
        GetPoolOffsetTrunkStartPosition() + _poolContentStartPos;

    private void ReadStringPoolInfo()
    {
        if (_contentTrunkLength == 0)
        {
            _poolColumnSize = 0;
            return;
        }

        var position = GetPoolOffsetTrunkStartPosition();
        if (position <= 0)
        {
            _poolColumnSize = 0;
            return;
        }

        _reader.Seek(position);

        int poolHeadLength;
        try
        {
            poolHeadLength = _reader.ReadI32();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Mirrors Python catching struct.error on a short read at EOF - no pool trailer present.
            _poolColumnSize = 0;
            return;
        }

        if (poolHeadLength <= 0)
        {
            _poolColumnSize = 0;
            return;
        }

        _poolColumnSize = _reader.ReadInt();
        if (_poolColumnSize <= 0)
        {
            return;
        }

        var stringPoolSize = _reader.ReadInt();
        var poolColumnLen = _reader.ReadInt();
        var poolOffsetTrunkLen = _reader.ReadInt();
        var poolInfoOffsetLen = poolHeadLength + 4;
        _poolContentStartPos = poolInfoOffsetLen + poolColumnLen + poolOffsetTrunkLen;

        if (poolColumnLen <= 0)
        {
            return;
        }

        _columnMap = [];
        for (var i = 0; i < _poolColumnSize; i++)
        {
            var columnIndex = _reader.ReadInt();
            _columnMap[columnIndex + 1] = true;
        }

        if (poolOffsetTrunkLen <= 0)
        {
            return;
        }

        // The offset trunk encoding changed in 4.5: older builds store LEB128
        // varints, 4.5+ stores fixed-width little-endian i32. The trunk byte
        // length tells us which - a fixed i32 array is exactly 4 bytes per
        // entry. Reading i32 data as LEB128 desyncs the array into sparse
        // junk and points pooled-string reads mid-character (mangled output).
        _poolOffsetInfoArray = [];
        if (poolOffsetTrunkLen == stringPoolSize * 4)
        {
            for (var i = 0; i < stringPoolSize; i++)
            {
                _poolOffsetInfoArray.Add(_reader.ReadI32());
            }
        }
        else
        {
            for (var i = 0; i < stringPoolSize; i++)
            {
                _poolOffsetInfoArray.Add(_reader.ReadInt());
            }
        }
    }

    private bool IsStringPoolColumn(int columnIndex)
    {
        if (!_enableStringPool)
        {
            return false;
        }

        if (columnIndex < 0)
        {
            return false;
        }

        if (_poolColumnSize is -1 or <= 0)
        {
            return false;
        }

        return _columnMap.GetValueOrDefault(columnIndex, false);
    }

    private string ReadPoolStringByIndex(int index)
    {
        if (_poolOffsetInfoArray.Count == 0)
        {
            return string.Empty;
        }

        if (index + 1 > _poolOffsetInfoArray.Count)
        {
            return string.Empty;
        }

        var poolContentStartPos = GetPoolContentTrunkStartPosition();
        var startPos = index <= 0 ? 0 : _poolOffsetInfoArray[index - 1];
        var currentPos = _reader.Position;

        _reader.Seek(poolContentStartPos + startPos);
        var stringValue = _reader.ReadString(forceUsePool: false);
        _reader.Seek(currentPos);

        return stringValue;
    }

    private void ReadContent()
    {
        if (_contentTrunkLength == 0 || _rowCount == 0)
        {
            _rows = [];
            return;
        }

        _reader.Seek(4 + _infoLength + _primaryKeyLength + _rowTrunkLength);

        _rows = new List<List<object>>(_rowCount);
        for (var i = 0; i < _rowCount; i++)
        {
            _rows.Add(ReadRow(i));
        }
    }

    private List<object> ReadRow(int rowIndex)
    {
        var row = new List<object>(_columns.Count);

        for (var j = 0; j < _columns.Count; j++)
        {
            var column = _columns[j];
            _reader.UseStringPool = _poolFlags[j];

            object value;
            try
            {
                value = _reader.ReadByColumnType(column.Type);
            }
            catch (Exception e) when (e is not BinaryTableException)
            {
                throw new BinaryTableException($"Error reading column {column.Name} at row {rowIndex}", e);
            }

            row.Add(value);

            switch (_columnKinds[j])
            {
                case ColumnKind.List:
                    var count = value switch
                    {
                        List<string> s => s.Count,
                        List<bool> b => b.Count,
                        List<int> i => i.Count,
                        List<double> d => d.Count,
                        List<object> o => o.Count,
                        List<List<object>> v => v.Count,
                        _ => 0,
                    };

                    if (count > column.ListLength)
                    {
                        column.ListLength = count;
                    }

                    break;

                case ColumnKind.IntDict:
                    var keys = value switch
                    {
                        Dictionary<int, int> dii => dii.Keys.AsEnumerable(),
                        Dictionary<int, string> dis => dis.Keys.AsEnumerable(),
                        Dictionary<int, double> dif => dif.Keys.AsEnumerable(),
                        _ => [],
                    };

                    foreach (var key in keys)
                    {
                        if (key > column.ListLength)
                        {
                            column.ListLength = key;
                        }
                    }

                    break;

                case ColumnKind.StrDict:
                    var strKeys = value switch
                    {
                        Dictionary<string, string> dss => dss.Keys.Cast<object>(),
                        Dictionary<string, int> dsi => dsi.Keys.Cast<object>(),
                        _ => [],
                    };

                    foreach (var key in strKeys)
                    {
                        column.AddDictKey(key);
                    }

                    break;
            }
        }

        return row;
    }

    private IEnumerable<string> CsvHeaders()
    {
        foreach (var column in _columns)
        {
            if (column.ListLength > 0)
            {
                for (var i = 0; i < column.ListLength; i++)
                {
                    yield return $"{column.Name}[{i}]";
                }
            }
            else if (column.IsDictType())
            {
                foreach (var key in column.DictKeys ?? [])
                {
                    yield return $"{column.Name}[{key}]";
                }
            }
            else
            {
                yield return column.Name;
            }
        }
    }

    private IEnumerable<string> CsvRow(List<object> row)
    {
        for (var key = 0; key < _columns.Count; key++)
        {
            var column = _columns[key];
            var value = row[key];

            if (column.IsIntKeyedDict())
            {
                var dict = ToIntKeyedObjectDict(value);
                for (var i = 0; i < column.ListLength; i++)
                {
                    yield return dict.TryGetValue(i + 1, out var v) ? FormatCell(v) : string.Empty;
                }
            }
            else if (column.ListLength > 0)
            {
                var list = ToObjectList(value);
                for (var i = 0; i < column.ListLength; i++)
                {
                    if (i >= list.Count)
                    {
                        yield return string.Empty;
                    }
                    else if (column.IsVectorList())
                    {
                        var vector = (List<object>)list[i];
                        yield return string.Join(';', vector.Select(FormatCell));
                    }
                    else
                    {
                        yield return FormatCell(list[i]);
                    }
                }
            }
            else if (column.IsDictType())
            {
                var dict = ToStringKeyedObjectDict(value);
                foreach (var k in column.DictKeys ?? [])
                {
                    yield return dict.TryGetValue((string)k, out var v) ? FormatCell(v) : string.Empty;
                }
            }
            else
            {
                yield return FormatCell(value);
            }
        }
    }

    private static Dictionary<int, object> ToIntKeyedObjectDict(object value) => value switch
    {
        Dictionary<int, int> d => d.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        Dictionary<int, string> d => d.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        Dictionary<int, double> d => d.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        _ => [],
    };

    private static Dictionary<string, object> ToStringKeyedObjectDict(object value) => value switch
    {
        Dictionary<string, string> d => d.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        Dictionary<string, int> d => d.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        _ => [],
    };

    private static List<object> ToObjectList(object value) => value switch
    {
        List<string> s => s.Cast<object>().ToList(),
        List<bool> b => b.Cast<object>().ToList(),
        List<int> i => i.Cast<object>().ToList(),
        List<double> d => d.Cast<object>().ToList(),
        List<object> o => o,
        List<List<object>> v => v.Cast<object>().ToList(),
        _ => [],
    };

    /// <summary>Mirrors Python's str() for the value types this format
    /// produces - notably bool -&gt; "True"/"False", not "true"/"false".</summary>
    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "True" : "False",
        string s => s,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Writes CSV matching Python's csv.writer defaults: comma delimiter,
    /// \r\n line terminator, minimal quoting (quote only fields containing
    /// the delimiter, a quote char, or a line break; double embedded quotes).
    /// </summary>
    public void ToCsv(TextWriter writer)
    {
        WriteCsvRow(writer, CsvHeaders());
        foreach (var row in _rows)
        {
            WriteCsvRow(writer, CsvRow(row));
        }
    }

    private static void WriteCsvRow(TextWriter writer, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                writer.Write(',');
            }

            first = false;
            writer.Write(QuoteCsvField(field));
        }

        writer.Write("\r\n");
    }

    private static string QuoteCsvField(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        var sb = new StringBuilder(field.Length + 2);
        sb.Append('"');
        foreach (var c in field)
        {
            if (c == '"')
            {
                sb.Append('"');
            }

            sb.Append(c);
        }

        sb.Append('"');
        return sb.ToString();
    }
}
