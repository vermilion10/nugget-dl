using System.Diagnostics;

namespace NuggetDl.Core.Video;

/// <summary>
/// Shells out to ffmpeg directly (no wrapper package) - a thin process
/// launcher, arg list, and stderr-on-failure capture is the entire surface
/// area the Python side used from ffmpeg-python too. Requires ffmpeg on
/// PATH.
/// </summary>
public static class FfmpegProcess
{
    public static async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg returned no process");
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("Failed to start ffmpeg - is it on PATH?", e);
        }

        using (process)
        {
            // Both streams have to be drained concurrently with the wait, not
            // after it: ffmpeg writes progress/diagnostics to stderr as it
            // runs, and a full pipe buffer would stall the process before it
            // ever exits.
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg failed (exit {process.ExitCode}): ffmpeg {string.Join(' ', arguments)}\n{stderr}");
            }
        }
    }

    private static readonly Dictionary<string, Task<bool>> EncoderAvailability = [];
    private static readonly object Gate = new();

    /// <summary>Probes whether a video encoder is available by running a
    /// one-frame throwaway encode against it, caching the result per encoder
    /// name for the process lifetime.</summary>
    public static Task<bool> CheckEncoderAvailableAsync(string encoder, CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (EncoderAvailability.TryGetValue(encoder, out var cached))
            {
                return cached;
            }

            var task = ProbeAsync(encoder, ct);
            EncoderAvailability[encoder] = task;
            return task;
        }
    }

    private static async Task<bool> ProbeAsync(string encoder, CancellationToken ct)
    {
        try
        {
            await RunAsync(
                ["-f", "lavfi", "-i", "color=c=black:s=320x240:d=0.1", "-f", "null", "-vcodec", encoder, "-"], ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
