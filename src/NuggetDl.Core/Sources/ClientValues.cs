namespace NuggetDl.Core.Sources;

/// <summary>
/// Every server address, app id and launcher key the tools use, plus the install's data folder name.
/// Private copy: the values are hardcoded here. The public copy has the same members but reads them from
/// client.json, so tools/export-public.sh never overwrites this file.
/// </summary>
public static class ClientValues
{
    private const string LauncherCdn = "https://cdn.invalid/";
    private const string CnLauncherCdn = "https://cdn.invalid/";

    public static bool IsConfigured => true;

    /// <summary>Unused here; kept so code that reports a missing client.json in the public copy compiles unchanged.</summary>
    public static string MissingMessage => "";

    /// <summary>The folder next to the game executable that holds StreamingAssets and resources.assets.</summary>
    public static string DataFolder => "Game_Data";

    // CDN base url, app_id, platform from the config tab PrimaryCdn, e.g.
    // https://cdn.invalid/
    // and for CN
    // https://cdn.invalid/
    public static IReadOnlyDictionary<string, PatchCdnData> PatchCdns { get; } =
        new Dictionary<string, PatchCdnData>(StringComparer.OrdinalIgnoreCase)
        {
            ["EN"] = new("https://cdn.invalid/", "app.id", "android"),
            ["EN_PC"] = new("https://cdn.invalid/", "app.id", "standalone"),
            ["KR"] = new("https://cdn.invalid/", "app.id", "android"),
            ["KR_PC"] = new("https://cdn.invalid/", "app.id", "standalone"),
            ["JP"] = new("https://cdn.invalid/", "app.id", "android"),
            ["JP_PC"] = new("https://cdn.invalid/", "app.id", "standalone"),
            ["TW"] = new("https://cdn.invalid/", "app.id", "android"),
            ["TW_PC"] = new("https://cdn.invalid/", "app.id", "standalone"),
            ["CN"] = new("https://cdn.invalid/", "app.id", "android"),
            ["CN_PC"] = new("https://cdn.invalid/", "app.id", "standalone"),
            ["CN_BETA"] = new("https://cdn.invalid/", "app.id", "android", Sign: true),
            ["CN_PC_BETA"] = new("https://cdn.invalid/", "app.id", "standalone", Sign: true),
        };

    public static IReadOnlyDictionary<string, PcStarterData> Launchers { get; } =
        new Dictionary<string, PcStarterData>(StringComparer.OrdinalIgnoreCase)
        {
            ["EN_PC"] = new(LauncherCdn, 143, "", Predownload: false),
            ["KR_PC"] = new(LauncherCdn, 286, "", Predownload: false),
            ["JP_PC"] = new(LauncherCdn, 282, "", Predownload: false),
            ["TW_PC"] = new(LauncherCdn, 279, "", Predownload: false),
            ["CN_PC"] = new(CnLauncherCdn, 148, "", Predownload: false),
            ["CN_PC_BETA"] = new(CnLauncherCdn, 148, "", Predownload: false),
        };
}
