using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;

namespace NuggetDl.Core.Spine;

public readonly record struct PointF(double X, double Y);

public readonly record struct SizeI(int Width, int Height);

/// <summary>A single Spine skeleton's data and output files.</summary>
public sealed class SpineInfo(string name, string innerName)
{
    public string Name { get; set; } = name;
    public string InnerName { get; } = innerName;
    public PointF? Position { get; set; }
    public SizeI? Size { get; set; }
    public double Scale { get; private set; } = 1;
    public PointF Pivot { get; set; } = new(0.5, 0.5);

    public int Order { get; set; }
    public HashSet<long> Ids { get; } = [];
    public long TransformId { get; set; }
    public string? DefaultAnimation { get; set; }

    /// <summary>(name, RGBA32 pixels, width, height) - RGBA pixels rather than
    /// a PIL Image, matching how NuggetDl.Core.Extraction already represents
    /// decoded textures.</summary>
    public List<(string Name, byte[] RgbaPixels, int Width, int Height)> Textures { get; } = [];

    /// <summary>
    /// Raw bytes rather than Python's str: Python stores this as text and,
    /// for the .skel (binary skeleton) case, round-trips it back to bytes via
    /// a surrogateescape encode at write time. Keeping it as bytes from the
    /// start avoids needing that workaround - Write() below decides text vs
    /// binary purely from the filename suffix.
    /// </summary>
    public byte[]? Json { get; set; }

    public string? Atlas { get; set; }

    public Dictionary<string, object?> Extra { get; } = [];

    public bool Valid() => Textures.Count > 0 && Json is not null && Atlas is not null;

    public override string ToString() =>
        $"Spine({Name} position={Position} size={Size} scale={Scale} anchor={Pivot} order={Order} tf={TransformId})";

    public void SetScale(double scale)
    {
        if (scale > 2)
        {
            scale /= 100;
        }

        Scale = scale;
    }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["innerName"] = InnerName,
        ["position"] = Position is { } p ? new JsonArray(p.X, p.Y) : null,
        ["size"] = Size is { } s ? new JsonArray(s.Width, s.Height) : null,
        ["scale"] = Scale,
        ["pivot"] = new JsonArray(Pivot.X, Pivot.Y),
        ["extra"] = Extra.Count == 0 ? new JsonObject() : JsonSerializer.SerializeToNode(Extra),
    };

    public void Write(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (Json is null || Atlas is null)
        {
            throw new InvalidOperationException("Write() called on an invalid (incomplete) SpineInfo");
        }

        var name = Name;
        if (name.EndsWith(".skel", StringComparison.Ordinal))
        {
            name = name[..^".skel".Length];
            File.WriteAllBytes(Path.Combine(outputDir, name + ".skel"), Json);
        }
        else
        {
            File.WriteAllText(Path.Combine(outputDir, name + ".json"), System.Text.Encoding.UTF8.GetString(Json));
        }

        File.WriteAllText(Path.Combine(outputDir, name + ".atlas"), Atlas);

        foreach (var (textureName, pixels, width, height) in Textures)
        {
            using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                pixels, width, height);
            img.SaveAsPng(Path.Combine(outputDir, textureName + ".png"));
        }
    }
}

public sealed class BoneFollower(string bone, string skeleton)
{
    public string Bone { get; } = bone;
    public string Skeleton { get; } = skeleton;
    public HashSet<long> Transforms { get; } = [];
    public HashSet<string> Spines { get; } = [];

    public JsonObject ToJson() => new()
    {
        ["bone"] = Bone,
        ["skeleton"] = Skeleton,
        ["spines"] = new JsonArray(Spines.OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
    };
}

/// <summary>
/// A named group of Spine skeletons extracted together (e.g. a character skin's separate body/face/weapon layers).
/// </summary>
public sealed class SpineGroup(string name)
{
    public string Name { get; } = name;
    public List<SpineInfo> Spines { get; set; } = [];
    public List<long>? SpineOrderList { get; set; }
    public SizeI? FoundSize { get; set; }
    public List<BoneFollower> BoneFollowers { get; set; } = [];
    public string? RenderQuirk { get; set; }

    public SizeI? Size()
    {
        if (FoundSize is { } found)
        {
            return found;
        }

        var sizes = Spines.Where(x => x.Size is not null).Select(x => x.Size!.Value).ToList();
        if (sizes.Count == 0)
        {
            return null;
        }

        return new SizeI(sizes.Max(s => s.Width), sizes.Max(s => s.Height));
    }

    public void Finalize_()
    {
        UpdateBoneFollowers();

        if (SpineOrderList is null)
        {
            Spines = Spines.OrderBy(x => x.Order).ToList();
        }
        else
        {
            FixActorSpine();
            var index = SpineOrderList
                .Select((id, i) => (id, i))
                .GroupBy(x => x.id)
                .ToDictionary(g => g.Key, g => g.First().i);

            Spines = Spines
                .OrderByDescending(x => x.Ids.Count == 0
                    ? double.PositiveInfinity
                    : x.Ids.Min(p => index.TryGetValue(p, out var i) ? i : double.PositiveInfinity))
                .ToList();
        }
    }

    private void UpdateBoneFollowers()
    {
        foreach (var follower in BoneFollowers)
        {
            foreach (var spine in Spines)
            {
                if (follower.Transforms.Contains(spine.TransformId))
                {
                    follower.Spines.Add(spine.Name);
                }
            }
        }

        BoneFollowers = BoneFollowers.Where(x => x.Spines.Count > 0).ToList();
    }

    private void FixActorSpine()
    {
        if (SpineOrderList is null)
        {
            return;
        }

        // First round of these was special.
        var isUimoviebiankaskin = Name.Contains("uimoviebiankaskin", StringComparison.Ordinal);
        if (isUimoviebiankaskin)
        {
            SpineOrderList.Reverse();
        }

        foreach (var spine in Spines)
        {
            if (!isUimoviebiankaskin)
            {
                spine.Position = new PointF(0, 0);
            }

            spine.SetScale(1);
            spine.Pivot = new PointF(0.5, 0);
        }
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["name"] = Name,
            ["spines"] = new JsonArray(Spines.Select(x => (JsonNode)x.ToJson()).ToArray()),
            ["size"] = Size() is { } s ? new JsonArray(s.Width, s.Height) : null,
            ["boneFollowers"] = new JsonArray(BoneFollowers.Select(x => (JsonNode)x.ToJson()).ToArray()),
        };

        if (RenderQuirk is not null)
        {
            obj["renderQuirk"] = RenderQuirk;
        }

        // Most common non-null default_animation across spines, ties broken by first-encountered order
        var mostCommon = Spines
            .Where(x => x.DefaultAnimation is not null)
            .Select(x => x.DefaultAnimation!)
            .Select((value, index) => (value, index))
            .GroupBy(x => x.value)
            .Select(g => (Value: g.Key, Count: g.Count(), FirstIndex: g.Min(x => x.index)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FirstIndex)
            .FirstOrDefault();

        if (mostCommon.Value is not null)
        {
            obj["defaultAnimation"] = mostCommon.Value;
        }

        return obj;
    }

    public void Write(string outputDir)
    {
        var subdir = Path.Combine(outputDir, Name);
        Directory.CreateDirectory(subdir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(subdir, "index.json"), ToJson().ToJsonString(options));

        foreach (var spine in Spines)
        {
            spine.Write(subdir);
        }
    }
}
