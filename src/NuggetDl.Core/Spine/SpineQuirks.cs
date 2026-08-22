using System.Text.RegularExpressions;

namespace NuggetDl.Core.Spine;

/// <summary>A per-name override for otherwise-unresolvable Spine metadata
/// (some skeletons have no size/scale info in the asset data itself).</summary>
public sealed record SpineQuirk(
    SizeI? Size = null,
    double? AllScale = null,
    PointF? AllPosition = null,
    PointF? AllPivot = null,
    string? Render = null);

/// <summary>Layers that should be combined into one, in order.</summary>
public sealed record GlueQuirk(string Name, IReadOnlyList<string> Layers);

/// <summary>
/// Pattern order matters - the first regex that matches (anchored at the start of the
/// string, not required to match the whole string) wins, so this is a list,
/// not a dictionary, to guarantee iteration order the way a C# Dictionary's
/// enumeration order technically isn't.
/// </summary>
public static class SpineQuirks
{
    private static readonly List<(string Pattern, SpineQuirk Quirk)> Quirks =
    [
        // Beeg Celica has no size
        ("sailika/sailika$", new SpineQuirk(Size: new SizeI(1520, 6340))),
        ("sailika/sailika2-rpgmakergame", new SpineQuirk(Size: new SizeI(256, 256))),
        ("kuluomu/kuluomu_tutorial/kuluomu_tutorial",
            new SpineQuirk(AllScale: 0.5, AllPosition: new PointF(10, -15))),
        ("main13_spine_luciya/main13_spine_luciya",
            new SpineQuirk(AllScale: 0.5, AllPosition: new PointF(0, -2))),
        ("main13_spine_luna/main13_spine_luna", new SpineQuirk(AllScale: 0.65)),
        ("luolan/luolantutorial/luolantutorial",
            new SpineQuirk(AllScale: 0.26, AllPosition: new PointF(0, 5))),
        ("nier-2bskin/spinenier2bskin1", new SpineQuirk(AllScale: 0.33, AllPosition: new PointF(40, -20))),
        ("uispine/uiturntable/uiturntablesailikacall", new SpineQuirk(Size: new SizeI(512, 512))),
        ("uilivwarmactivityvinylspine", new SpineQuirk(Size: new SizeI(200, 200))),
        ("uispine/uisummersigninmainspine/bg/uisummerspinebg",
            new SpineQuirk(Size: new SizeI(3800, 1080), AllScale: 0.5)),
        ("2-11spine/lamiyaskin/lamiyaskinautowindow",
            new SpineQuirk(AllScale: 0.45, Size: new SizeI(1518, 856))),
        ("lifu/chaolifudarkskin/chaolifudarkskinwindow", new SpineQuirk(Size: new SizeI(1518, 856))),
        ("luciya/luciyasairen/luciyasairendrawcoating", new SpineQuirk(Size: new SizeI(1518, 856))),
        ("luxiya-skin-qingrenjie/spineluxiyaskin", new SpineQuirk(Size: new SizeI(1518, 856))),
        (".*autowindow", new SpineQuirk(Size: new SizeI(1518, 856))),
        ("selena/selenaactivity", new SpineQuirk(AllScale: 0.5)),
        ("activityspine/activitybianka/activitybianka$", new SpineQuirk(AllScale: 0.3, Render: "short")),
        ("activityspine/activitybuouxiong/activitybuouxiong", new SpineQuirk(AllPosition: new PointF(0, 0))),
        ("activityspine/activityhanying/activityhanying", new SpineQuirk(AllScale: 0.5)),
        ("activityspine/activityli/activitylifuben", new SpineQuirk(AllScale: 0.5)),
        ("activityspine/activity21/activity21", new SpineQuirk(AllScale: 0.26)),
        ("activityspine/activityspinechunjieqishi", new SpineQuirk(Render: "short")),
        ("uispine/uigoldenminerspine", new SpineQuirk(AllScale: 0.14, Size: new SizeI(100, 100))),
        ("2-12spine/uihitmouse2-12spine",
            new SpineQuirk(AllScale: 1, Size: new SizeI(1000, 1000), AllPivot: new PointF(0.5, 0.5))),
        ("uihitmousespine", new SpineQuirk(AllScale: 1, Size: new SizeI(1000, 1000), AllPivot: new PointF(0.5, 0.5))),
        // Very old login screens
        ("spinelogin/1-18",
            new SpineQuirk(Size: new SizeI(1920, 1080), AllPivot: new PointF(0.5, 0.5), AllPosition: new PointF(6, 20))),
        ("spinelogin/1-21",
            new SpineQuirk(Size: new SizeI(1920, 1080), AllPivot: new PointF(0.5, 0.5), AllPosition: new PointF(0, -90))),
        ("spinelogin/1-22",
            new SpineQuirk(Size: new SizeI(1920, 1080), AllPivot: new PointF(0.5, 0.5), AllPosition: new PointF(0, 40))),
        ("spinelogin/1-*", new SpineQuirk(Size: new SizeI(1920, 1080), AllPivot: new PointF(0.5, 0.5))),
    ];

    private static readonly Dictionary<string, GlueQuirk> QuirksGlue = new()
    {
        ["selena/selenaactivity/selenaactivitybg"] = new GlueQuirk(
            "selena/selenaactivity",
            ["selena/selenaactivity/selenaactivitybg", "selena/selenaactivity/selenaactivityqg"]),
        ["activityspine/activityspineyuandan/activityspineyuandanbg"] = new GlueQuirk(
            "activityspine/activityspineyuandan",
            [
                "activityspine/activityspineyuandan/activityspineyuandanbg",
                "activityspine/activityspineyuandan/activityspineyuandanqg",
            ]),
        ["activityspine/activityspinechunjieqishi/activityqishibg"] = new GlueQuirk(
            "activityspine/activityspinechunjieqishi",
            [
                "activityspine/activityspinechunjieqishi/activityqishibg",
                "activityspine/activityspinechunjieqishi/activityqishirole",
                "activityspine/activityspinechunjieqishi/activityqishiqg",
            ]),
        ["activityspine/activityhakama/activityhakamabg"] = new GlueQuirk(
            "activityspine/activityhakama",
            ["activityspine/activityhakama/activityhakamabg", "activityspine/activityhakama/activityhakamaqg"]),
    };

    private static readonly HashSet<string> QuirksSkip =
        QuirksGlue.Values
            .SelectMany(q => q.Layers)
            .Where(layer => !QuirksGlue.ContainsKey(layer))
            .ToHashSet();

    public static SpineQuirk? FindQuirk(string name)
    {
        foreach (var (pattern, quirk) in Quirks)
        {
            // Python's re.match anchors at the start of the string only (not
            // the end) - `^` here reproduces exactly that, no `$` needed.
            if (Regex.IsMatch(name, "^(?:" + pattern + ")"))
            {
                return quirk;
            }
        }

        return null;
    }

    public static void ApplyQuirk(SpineGroup spine)
    {
        var quirk = FindQuirk(spine.Name);
        if (quirk is null)
        {
            return;
        }

        if (quirk.AllScale is { } allScale)
        {
            foreach (var s in spine.Spines)
            {
                s.SetScale(allScale);
            }
        }

        if (quirk.Size is { } size)
        {
            spine.FoundSize = size;
        }

        if (quirk.AllPosition is { } allPosition)
        {
            foreach (var s in spine.Spines)
            {
                s.Position = allPosition;
            }
        }

        if (quirk.AllPivot is { } allPivot)
        {
            foreach (var s in spine.Spines)
            {
                s.Pivot = allPivot;
            }
        }

        if (quirk.Render is not null)
        {
            spine.RenderQuirk = quirk.Render;
        }
    }

    public static bool ShouldSkip(string name) => QuirksSkip.Contains(name);

    public static GlueQuirk? FindGlue(string name) => QuirksGlue.GetValueOrDefault(name);
}
