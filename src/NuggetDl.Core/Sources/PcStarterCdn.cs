namespace NuggetDl.Core.Sources;

/// <summary>Settings for a PC starter/launcher CDN.</summary>
public sealed record PcStarterData(string Cdn, int GameId, string Iteration, bool Predownload)
{
    public string IndexUrl() => $"{Cdn}game/G{GameId}/{Iteration}/index.json";
}

/// <summary>The known PC starter/launcher CDNs. The table lives in <see cref="ClientValues"/>.</summary>
public static class PcStarterCdn
{
    /// <summary>Name -&gt; data, for CLI/SourceSet lookups by string (e.g. "EN_PC").</summary>
    public static IReadOnlyDictionary<string, PcStarterData> ByName => ClientValues.Launchers;
}
