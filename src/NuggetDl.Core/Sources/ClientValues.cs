using System.Globalization;
using System.Text.Json;

namespace NuggetDl.Core.Sources;

/// <summary>
/// Every server address, app id, launcher key and decryption key the tools use, plus the install's data folder
/// name. None of these ship with the source: they're read from client.json (fields as in client.example.json),
/// looked for next to the executable, then in the user's application-data folder (nugget-dl/client.json).
/// Without the file the tables are empty, and source lookups report <see cref="MissingMessage"/>.
/// </summary>
public static class ClientValues
{
    public const string FileName = "client.json";

    // Declared before the values below, which read it while the type initializes.
    public static IReadOnlyList<string> SearchPaths { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nugget-dl", FileName),
    ];

    private static readonly ClientFile? Config = Load();

    public static bool IsConfigured => Config is not null;

    public static string MissingMessage =>
        $"No {FileName} found. Copy client.example.json to {SearchPaths[^1]} and fill in the server addresses and keys.";

    /// <summary>The folder next to the game executable that holds StreamingAssets and resources.assets.</summary>
    public static string DataFolder => Config?.DataFolder ?? "";

    public static IReadOnlyDictionary<string, PatchCdnData> PatchCdns { get; } =
        (Config?.PatchCdns ?? []).ToDictionary(
            p => p.Key, p => new PatchCdnData(p.Value.Cdn, p.Value.AppId, p.Value.Platform, Sign: p.Value.Sign),
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, PcStarterData> Launchers { get; } =
        (Config?.Launchers ?? []).ToDictionary(
            p => p.Key, p => new PcStarterData(p.Value.Cdn, p.Value.GameId, p.Value.Iteration, Predownload: false),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>UnityCN bundle keys, checked newest-first; the first entry whose version is &lt;= the game version wins.</summary>
    public static IReadOnlyList<(Version MinVersion, string Key)> BundleKeys { get; } =
        (Config?.BundleKeys ?? []).Select(k => (Version.Parse(k.MinVersion), k.Key)).OrderByDescending(k => k.Item1).ToList();

    /// <summary>Fixed HCA decryption key used for every ACB/AWB/USM audio stream.</summary>
    public static long AudioKey => Config?.AudioKey is { Length: > 0 } key
        ? key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(key, CultureInfo.InvariantCulture)
        : 0;

    /// <summary>The XOR table obfuscated text assets under temp/bytes are encrypted with.</summary>
    public static byte[] TextKey { get; } = Config?.TextKey is { Length: > 0 } hex ? Convert.FromHexString(hex) : [0];

    private static ClientFile? Load()
    {
        if (SearchPaths.FirstOrDefault(File.Exists) is not { } path)
        {
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
            return JsonSerializer.Deserialize<ClientFile>(File.ReadAllText(path), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ClientFile(
        string? DataFolder,
        Dictionary<string, PatchEntry>? PatchCdns,
        Dictionary<string, LauncherEntry>? Launchers,
        List<BundleKeyEntry>? BundleKeys,
        string? AudioKey,
        string? TextKey);

    private sealed record PatchEntry(string Cdn, string AppId, string Platform, bool Sign);

    private sealed record LauncherEntry(string Cdn, int GameId, string Iteration);

    private sealed record BundleKeyEntry(string MinVersion, string Key);
}
