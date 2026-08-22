namespace NuggetDl.Core.BinaryTable;

public sealed class Column
{
    public string Name { get; }
    public int Type { get; }
    public int ListLength { get; set; }
    public List<object>? DictKeys { get; private set; }
    private HashSet<object>? _dictKeyPresence;

    public Column(string name, int type)
    {
        Name = name;
        Type = type;
        ListLength = 0;
    }

    public bool IsDictType() => Type is >= 9 and <= 13;

    public bool IsIntKeyedDict() => Type is 10 or 11 or 13;

    /// <summary>ListFix2 / ListFix3 / ListFixQuaternion: each element is itself a vector.</summary>
    public bool IsVectorList() => Type is >= 19 and <= 21;

    public void AddDictKey(object key)
    {
        DictKeys ??= [];
        _dictKeyPresence ??= [];

        if (_dictKeyPresence.Add(key))
        {
            DictKeys.Add(key);
        }
    }
}
