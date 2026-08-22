namespace NuggetDl.Core.Video;

/// <summary>Muxes demuxed streams into a faststart MP4 (H.264 video, MP3 audio).</summary>
public sealed class Mp4Encoder : IVideoEncoder
{
    private string _encoder = "h264";

    public async Task SetupAsync(CancellationToken ct = default)
    {
        if (await FfmpegProcess.CheckEncoderAvailableAsync("h264_nvenc", ct))
        {
            _encoder = "h264_nvenc";
        }
    }

    public async Task EncodeAsync(
        string baseOutputPath, IReadOnlyList<VideoTrack> video, IReadOnlyList<VideoTrack> audio,
        CancellationToken ct = default)
    {
        var outputFile = baseOutputPath + ".mp4";
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        var args = new List<string> { "-y", "-hwaccel", "auto" };
        foreach (var v in video)
        {
            args.Add("-i");
            args.Add(v.Path);
        }

        foreach (var a in audio)
        {
            args.Add("-i");
            args.Add(a.Path);
        }

        var totalInputs = video.Count + audio.Count;
        for (var i = 0; i < totalInputs; i++)
        {
            args.Add("-map");
            args.Add(i.ToString());
        }

        args.AddRange(["-c:v", _encoder, "-movflags", "+faststart", "-c:a", "mp3", "-ar", "44100", "-q:a", "2"]);

        for (var i = 0; i < audio.Count; i++)
        {
            if (audio[i].Language is { } language)
            {
                args.Add($"-metadata:s:a:{i}");
                args.Add($"language={language}");
            }
        }

        args.Add(outputFile);

        await FfmpegProcess.RunAsync(args, ct);
    }
}
