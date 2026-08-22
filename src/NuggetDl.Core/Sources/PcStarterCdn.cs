namespace NuggetDl.Core.Sources;

/// <summary>Settings for a PC starter/launcher CDN.</summary>
public sealed record PcStarterData(string Cdn, int GameId, string Iteration, bool Predownload)
{
    public string IndexUrl() => $"{Cdn}game/G{GameId}/{Iteration}/index.json";
}

/// <summary>The known PC starter/launcher CDNs.</summary>
public static class PcStarterCdn
{
    private const string LauncherCdn = "https://cdn.invalid/";
    private const string CnLauncherCdn = "https://cdn.invalid/";

    public static readonly PcStarterData EnPc = new(
        LauncherCdn, 143, "", Predownload: false);

    public static readonly PcStarterData KrPc = new(
        LauncherCdn, 286, "", Predownload: false);

    public static readonly PcStarterData JpPc = new(
        LauncherCdn, 282, "", Predownload: false);

    public static readonly PcStarterData TwPc = new(
        LauncherCdn, 279, "", Predownload: false);

    public static readonly PcStarterData CnPc = new(
        CnLauncherCdn, 148, "", Predownload: false);

    public static readonly PcStarterData CnPcBeta = new(
        CnLauncherCdn, 148, "", Predownload: false);

    /// <summary>Name -&gt; data, for CLI/SourceSet lookups by string (e.g. "EN_PC").</summary>
    public static readonly IReadOnlyDictionary<string, PcStarterData> ByName =
        new Dictionary<string, PcStarterData>(StringComparer.OrdinalIgnoreCase)
        {
            ["EN_PC"] = EnPc,
            ["KR_PC"] = KrPc,
            ["JP_PC"] = JpPc,
            ["TW_PC"] = TwPc,
            ["CN_PC"] = CnPc,
            ["CN_PC_BETA"] = CnPcBeta,
        };
}
