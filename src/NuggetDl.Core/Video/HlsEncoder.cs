namespace NuggetDl.Core.Video;

/// <summary>
/// Muxes demuxed streams into an HLS variant playlist, one rendition per audio language.
/// </summary>
public sealed class HlsEncoder : IVideoEncoder
{
    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["ja"] = "Japanese",
        ["en"] = "English",
        ["zh"] = "Chinese",
        ["yue"] = "Cantonese",
    };

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
        var streamName = Path.GetFileName(baseOutputPath);
        var streamDir = Path.Combine(Path.GetDirectoryName(baseOutputPath) ?? "", "streams", streamName);
        Directory.CreateDirectory(streamDir);

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

        var varStreamMap = new List<string>();
        var audioNames = new Dictionary<int, string>();

        for (var i = 0; i < video.Count; i++)
        {
            varStreamMap.Add($"v:{i},name:video{i},agroup:audio");
        }

        for (var i = 0; i < audio.Count; i++)
        {
            var kvs = new List<string> { $"a:{i}", "agroup:audio", $"default:{(i == 0 ? "yes" : "no")}" };
            var name = $"audio{i}";
            if (audio[i].Language is { } language)
            {
                name = language;
                kvs.Add("language:" + language);
            }

            kvs.Add($"name:{name}");
            audioNames[i] = LanguageNames.GetValueOrDefault(name, name);
            varStreamMap.Add(string.Join(",", kvs));
        }

        args.AddRange([
            "-c:v", _encoder,
            "-c:a", "aac",
            "-b:a", "96k",
            "-q:a", "2",
            "-hls_time", "2",
            "-hls_playlist_type", "vod",
            "-master_pl_name", "master.m3u8",
            "-hls_segment_filename", Path.Combine(streamDir, "%v.%d.ts"),
            "-var_stream_map", string.Join(" ", varStreamMap),
        ]);
        args.Add(Path.Combine(streamDir, "%v.m3u8"));

        await FfmpegProcess.RunAsync(args, ct);

        // ffmpeg names HLS audio renditions "audio_N" in the master playlist
        // regardless of the `name:` we gave each one (https://trac.ffmpeg.org/ticket/11560)
        // patch it afterward
        var masterPath = Path.Combine(streamDir, "master.m3u8");
        var content = await File.ReadAllTextAsync(masterPath, ct);
        foreach (var (i, name) in audioNames)
        {
            content = content.Replace($"audio_{i + 1}", name);
        }

        await File.WriteAllTextAsync(masterPath, content, ct);
    }
}
