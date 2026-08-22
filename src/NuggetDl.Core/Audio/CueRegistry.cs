using System.Text;
using NuggetDl.Core.Extraction;
using NuggetDl.Core.Sources;

namespace NuggetDl.Core.Audio;

/// <summary>One row of assets/temp/bytes/share|client/audio/cuesheet.tab.bytes:
/// maps a game-referenced ACB bundle to its (optional) companion AWB bundle.
/// </summary>
public sealed record CueSheetEntry(int Id, string Acb, string Awb)
{
    public string BaseName => DeriveBaseName(Acb);

    /// <summary>Derives the base name from a bundle path.</summary>
    public static string DeriveBaseName(string bundlePath)
    {
        var parts = bundlePath.Split('/', 3);
        var afterPrefix = parts.Length == 3 ? parts[2] : bundlePath;
        return afterPrefix.Split('.')[0].ToLowerInvariant();
    }
}

/// <summary>Maps ACB bundle names to their cue sheet entry (and thus their
/// companion AWB, if any) by reading and binary-table-decoding the game's
/// own cue sheet.
/// </summary>
public sealed class CueRegistry
{
    private const string ShareAudioBundle = "assets/temp/bytes/share/audio.ab";
    private const string ClientAudioBundle = "assets/temp/bytes/client/audio.ab";
    private const string CueSheetSuffix = "cuesheet.tab.bytes";

    private Dictionary<string, CueSheetEntry> _byAcb = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitAsync(
        SourceSet sources, IBundleContentReader contentReader, Version gameVersion, CancellationToken ct = default)
    {
        byte[] audioBundleBytes;
        try
        {
            audioBundleBytes = await sources.FindBundleAsync(ShareAudioBundle, ct);
        }
        catch (BlobNotFoundException)
        {
            // Servers host the cue sheet under either share/ or client/; fall
            // back to client/ if share/ is absent or unreachable.
            audioBundleBytes = await sources.FindBundleAsync(ClientAudioBundle, ct);
        }

        var assets = contentReader.LoadContents(audioBundleBytes);
        var cueSheetAsset = assets.OfType<TextAssetContent>()
            .FirstOrDefault(a => a.ContainerPath.EndsWith(CueSheetSuffix, StringComparison.Ordinal));
        if (cueSheetAsset is null)
        {
            throw new InvalidOperationException($"{CueSheetSuffix} not found in the audio bundle");
        }

        var rewritten = TextAssetRewriter.Rewrite(
            cueSheetAsset.ContainerPath, cueSheetAsset.Script, gameVersion, allowBinaryTableConvert: true);
        var csv = Encoding.UTF8.GetString(rewritten.Data);

        var entries = new Dictionary<string, CueSheetEntry>(StringComparer.OrdinalIgnoreCase);
        var lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1)) // header row
        {
            var fields = line.Split(',');
            if (fields.Length < 3 || !int.TryParse(fields[0], out var id))
            {
                continue;
            }

            var entry = new CueSheetEntry(id, fields[1], fields[2]);
            entries[entry.Acb] = entry;
        }

        _byAcb = entries;
    }

    public CueSheetEntry? GetCueSheet(string acbBundle) => _byAcb.GetValueOrDefault(acbBundle);
}
