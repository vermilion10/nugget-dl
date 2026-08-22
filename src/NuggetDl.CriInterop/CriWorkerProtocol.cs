using MessagePack;

namespace NuggetDl.CriInterop;

/// <summary>
/// Wire contract for tools/cri_worker/worker.py. Every property carries an
/// explicit lowercase [Key] so the msgpack map keys match the Python side's
/// plain dicts exactly - both ends read/write the same map, no naming
/// convention translation.
/// </summary>
[MessagePackObject]
public sealed class CriRequest
{
    [Key("id")]
    public int Id { get; set; }

    [Key("op")]
    public string Op { get; set; } = "";

    [Key("acb")]
    public byte[]? Acb { get; set; }

    [Key("awb")]
    public byte[]? Awb { get; set; }

    [Key("usm")]
    public byte[]? Usm { get; set; }

    [Key("key")]
    public long Key { get; set; }
}

[MessagePackObject]
public sealed class CriResponse
{
    [Key("id")]
    public int Id { get; set; }

    [Key("ok")]
    public bool Ok { get; set; }

    [Key("error")]
    public string? Error { get; set; }

    [Key("tracks")]
    public List<CriTrackDto>? Tracks { get; set; }
}

/// <summary>A single decoded/demuxed stream. decode_acb only ever sets
/// Name+Wav; demux_usm sets Name+Kind+Language+Data.</summary>
[MessagePackObject]
public sealed class CriTrackDto
{
    [Key("name")]
    public string Name { get; set; } = "";

    [Key("wav")]
    public byte[]? Wav { get; set; }

    [Key("kind")]
    public string? Kind { get; set; }

    [Key("language")]
    public string? Language { get; set; }

    [Key("data")]
    public byte[]? Data { get; set; }
}
