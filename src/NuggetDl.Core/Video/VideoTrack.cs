namespace NuggetDl.Core.Video;

/// <summary>One demuxed stream sitting on disk, ready to hand to ffmpeg.</summary>
public sealed record VideoTrack(string Name, string Path, string? Language = null);
