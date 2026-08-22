namespace NuggetDl.Core.Video;

public interface IVideoEncoder
{
    /// <summary>
    /// Called once, only if there's at least one video to encode - probes for hardware encoder support.
    /// </summary>
    Task SetupAsync(CancellationToken ct = default);

    /// <summary>
    /// Muxes the given tracks into baseOutputPath - no extension; each
    /// encoder appends its own (and, for HLS, a whole directory of
    /// segments/playlists alongside it).
    /// </summary>
    Task EncodeAsync(
        string baseOutputPath, IReadOnlyList<VideoTrack> video, IReadOnlyList<VideoTrack> audio,
        CancellationToken ct = default);
}
