namespace NuggetDl.Core;

public static class Versions
{
    /// <summary>
    /// The patch CDN URL scheme changed in 4.3.0 ("A Better Tomorrow"): from
    /// this version onward a per-build key segment is inserted into the
    /// config/patch URLs.
    /// </summary>
    public static readonly Version PatchKeySchemeMinVersion = new(4, 3, 0);

    /// <summary>
    /// Parses a dotted version string like "4.3.0" into a <see cref="Version"/>.
    /// System.Version requires at least two components, so a bare "4" is
    /// normalized to "4.0" to match the permissiveness of the original
    /// tuple-based parser.
    /// </summary>
    public static Version Parse(string version)
    {
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => new Version(int.Parse(parts[0]), 0),
            _ => Version.Parse(version),
        };
    }
}
