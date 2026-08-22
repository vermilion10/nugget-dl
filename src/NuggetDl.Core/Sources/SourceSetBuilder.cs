namespace NuggetDl.Core.Sources;

public static class Presets
{
    public static readonly IReadOnlyDictionary<string, string> ByName = new Dictionary<string, string>
    {
        ["global"] = "EN_PC",
        ["korea"] = "KR_PC",
        ["japan"] = "JP_PC",
        ["taiwan"] = "TW_PC",
        ["china"] = "CN_PC",
        ["china-beta"] = "CN_PC_BETA",
    };

    // Checked newest-first; the first entry whose version is <= the game
    // version wins.
    private static readonly (Version MinVersion, string Key)[] DecryptionKeys =
    [
        (new Version(3, 4, 0), ""),
        (new Version(1, 22, 0), ""),
        (new Version(0, 0, 0), ""),
    ];

    public static string DetermineDecryptionKey(Version version)
    {
        foreach (var (minVersion, key) in DecryptionKeys)
        {
            if (version >= minVersion)
            {
                return key;
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Outcome of <see cref="SourceSetBuilder.BuildAsync"/>: the configured
/// source set plus the resolved game version and decryption key.
/// </summary>
public sealed record ResolvedSources(SourceSet Sources, Version Version, string DecryptKey);

public sealed record SourceSetOptions(
    string? Preset = null,
    bool Prerelease = false,
    string? Primary = null,
    string? Obb = null,
    string? GameDir = null,
    string? Patch = null,
    string? Version = null,
    string? DecryptKeyOverride = null);

public static class SourceSetBuilder
{
    public static async Task<ResolvedSources> BuildAsync(
        SourceSetOptions options, IBundleIndexReader bundleIndexReader, CancellationToken ct = default)
    {
        var preset = options.Preset;
        var primary = options.Primary;
        var patch = options.Patch;

        // Default to global when no source is specified; --primary/--patch opts out.
        if (preset is null && primary is null && patch is null)
        {
            preset = "global";
        }

        if (preset is not null)
        {
            if (!Presets.ByName.TryGetValue(preset, out var presetSource))
            {
                throw new UnknownSourceException($"Unknown preset {preset}");
            }

            primary = presetSource;
            patch = presetSource;
        }

        if (primary is null)
        {
            throw new ArgumentException("No primary source specified (use --preset or --primary)");
        }

        if (options.Obb is not null && options.Version is null)
        {
            throw new ArgumentException("Version must be specified when using an obb file as the primary source");
        }

        if (primary == "local" && options.GameDir is null)
        {
            throw new ArgumentException("--game-dir is required when the primary source is 'local'");
        }

        if (options.GameDir is not null && primary != "local")
        {
            throw new ArgumentException("--game-dir is only valid together with --primary local");
        }

        var sourceSet = new SourceSet(bundleIndexReader);

        var explicitVersion = options.Version is not null ? Versions.Parse(options.Version) : null;

        await sourceSet.AddPrimaryAsync(primary, options.Obb, options.Prerelease, options.GameDir, ct);

        var version = explicitVersion;
        if (version is null)
        {
            var inferred = await sourceSet.VersionAsync(ct);
            if (inferred is null)
            {
                throw new VersionNotDeterminableException(primary);
            }

            // Only major.minor.build matters from here on (drop any 4th component).
            version = new Version(inferred.Major, inferred.Minor, inferred.Build < 0 ? 0 : inferred.Build);
        }

        var decryptKey = options.DecryptKeyOverride ?? Presets.DetermineDecryptionKey(version);
        bundleIndexReader.SetDecryptKey(decryptKey);

        if (patch is not null)
        {
            await sourceSet.AddPatchAsync(patch, options.Version, ct);
        }

        if (string.IsNullOrEmpty(decryptKey))
        {
            throw new DecryptionKeyNotDeterminableException();
        }

        return new ResolvedSources(sourceSet, version, decryptKey);
    }
}
