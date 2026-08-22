namespace NuggetDl.Core.Sources;

/// <summary>
/// Settings required to use a patch CDN for a server.
/// </summary>
public sealed record PatchCdnData(
    string Cdn,
    string AppId,
    string Platform,
    string? Key = null,
    bool Sign = false)
{
    public string ConfigUrl(string version)
    {
        var path = new List<string> { Cdn, "client/config" };

        if (Key is not null && Versions.Parse(version) >= Versions.PatchKeySchemeMinVersion)
        {
            path.Add(Key);
        }

        path.AddRange([AppId, version, Platform, "config.tab"]);
        return string.Join('/', path);
    }

    public string BaseUrl(string applicationVersion, string documentVersion)
    {
        var path = new List<string> { Cdn, "client/patch" };

        if (Key is not null && Versions.Parse(applicationVersion) >= Versions.PatchKeySchemeMinVersion)
        {
            path.Add(Key);
        }

        path.AddRange([AppId, applicationVersion, Platform, documentVersion, "matrix/"]);
        return string.Join('/', path);
    }

    /// <summary>Returns a copy of this data with a different patch key.</summary>
    public PatchCdnData WithKey(string key) => this with { Key = key };
}

/// <summary>
/// The known patch CDNs, one per region/platform combination.
/// </summary>
public static class PatchCdn
{
    // CDN base url, app_id, platform from the config tab PrimaryCdn, e.g.
    // https://cdn.invalid/
    public static readonly PatchCdnData En = new(
        "https://cdn.invalid/",
        "app.id",
        "android");

    public static readonly PatchCdnData EnPc = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone");

    public static readonly PatchCdnData Kr = new(
        "https://cdn.invalid/",
        "app.id",
        "android");

    public static readonly PatchCdnData KrPc = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone");

    public static readonly PatchCdnData Jp = new(
        "https://cdn.invalid/",
        "app.id",
        "android");

    public static readonly PatchCdnData JpPc = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone");

    public static readonly PatchCdnData Tw = new(
        "https://cdn.invalid/",
        "app.id",
        "android");

    public static readonly PatchCdnData TwPc = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone");

    // https://cdn.invalid/
    public static readonly PatchCdnData Cn = new(
        "https://cdn.invalid/",
        "app.id",
        "android");

    public static readonly PatchCdnData CnPc = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone");

    public static readonly PatchCdnData CnBeta = new(
        "https://cdn.invalid/",
        "app.id",
        "android",
        Sign: true);

    public static readonly PatchCdnData CnPcBeta = new(
        "https://cdn.invalid/",
        "app.id",
        "standalone",
        Sign: true);

    /// <summary>Name -&gt; data, for CLI lookups by string (e.g. "EN_PC").</summary>
    public static readonly IReadOnlyDictionary<string, PatchCdnData> ByName =
        new Dictionary<string, PatchCdnData>(StringComparer.OrdinalIgnoreCase)
        {
            ["EN"] = En,
            ["EN_PC"] = EnPc,
            ["KR"] = Kr,
            ["KR_PC"] = KrPc,
            ["JP"] = Jp,
            ["JP_PC"] = JpPc,
            ["TW"] = Tw,
            ["TW_PC"] = TwPc,
            ["CN"] = Cn,
            ["CN_PC"] = CnPc,
            ["CN_BETA"] = CnBeta,
            ["CN_PC_BETA"] = CnPcBeta,
        };
}
