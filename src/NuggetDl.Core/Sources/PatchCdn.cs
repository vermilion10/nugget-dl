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
/// The known patch CDNs, one per region/platform combination. The table lives in <see cref="ClientValues"/>.
/// </summary>
public static class PatchCdn
{
    /// <summary>Name -&gt; data, for CLI lookups by string (e.g. "EN_PC").</summary>
    public static IReadOnlyDictionary<string, PatchCdnData> ByName => ClientValues.PatchCdns;
}
