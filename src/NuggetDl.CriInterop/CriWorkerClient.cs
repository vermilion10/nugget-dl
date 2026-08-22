using System.Buffers;
using System.Diagnostics;
using MessagePack;

namespace NuggetDl.CriInterop;

/// <summary>A decoded ACB cue's waveform, as a full WAV file.</summary>
public sealed record AudioTrack(string Name, byte[] Wav);

/// <summary>One demuxed USM stream. Kind is "video" or "audio"; Language is
/// only set for audio tracks where the CRID filename carried a recognizable
/// language tag. Data is raw video bytes for video, decoded WAV for audio</summary>
public sealed record UsmTrack(string Name, string Kind, string? Language, byte[] Data);

/// <summary>
/// Talks to a persistent tools/cri_worker/worker.py process over msgpack on
/// stdin/stdout. Kept alive across calls (not spawned per-bundle) so `uv`'s
/// dependency resolution and PyCriCodecsEx's import cost are paid once per
/// extract run, not once per bundle. Not thread-safe for concurrent calls -
/// requests are serialized one at a time (a lock, not a pool), matching the
/// worker's own single-threaded request loop.
/// </summary>
public sealed class CriWorkerClient : IAsyncDisposable
{
    private readonly Process _process;

    // MessagePackSerializer.DeserializeAsync(Stream) does not work against a
    // live, non-seekable process pipe on this .NET/Windows combination - it
    // hangs indefinitely even once the worker has written and flushed a
    // reply (confirmed: a raw Stream.ReadAsync on the same pipe returns the
    // bytes immediately). MessagePackStreamReader, built for exactly this
    // "read one framed value at a time off a live stream" case, does not
    // have the problem - use it for every read instead.
    private readonly MessagePackStreamReader _responseReader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private CriWorkerClient(Process process)
    {
        _process = process;
        _responseReader = new MessagePackStreamReader(process.StandardOutput.BaseStream, leaveOpen: true);
    }

    /// <summary>Starts the worker via `uv run &lt;worker.py&gt;`. uv resolves
    /// worker.py's inline PEP 723 dependencies into a cached venv on first
    /// run (network access required then; cached and fast afterward).</summary>
    public static CriWorkerClient Start(string? workerScriptPath = null)
    {
        var scriptPath = workerScriptPath ?? ResolveDefaultWorkerScript();

        var psi = new ProcessStartInfo
        {
            FileName = "uv",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // inherited: worker logs go straight to our stderr
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(scriptPath);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the CRI worker process (uv run returned no process)");

        return new CriWorkerClient(process);
    }

    private static string ResolveDefaultWorkerScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cri_worker", "worker.py");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "CRI worker script not found next to the build output. Expected it at: " + path, path);
        }

        return path;
    }

    public async Task<List<AudioTrack>> DecodeAcbAsync(byte[] acb, byte[]? awb, long key, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new CriRequest { Op = "decode_acb", Acb = acb, Awb = awb, Key = key }, ct);

        return (response.Tracks ?? [])
            .Select(t => new AudioTrack(t.Name, t.Wav ?? []))
            .ToList();
    }

    public async Task<List<UsmTrack>> DemuxUsmAsync(byte[] usm, long key, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new CriRequest { Op = "demux_usm", Usm = usm, Key = key }, ct);

        return (response.Tracks ?? [])
            .Select(t => new UsmTrack(t.Name, t.Kind ?? "unknown", t.Language, t.Data ?? []))
            .ToList();
    }

    private async Task<CriResponse> SendAsync(CriRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            request.Id = ++_nextId;

            await MessagePackSerializer.SerializeAsync(_process.StandardInput.BaseStream, request, cancellationToken: ct);
            await _process.StandardInput.BaseStream.FlushAsync(ct);

            ReadOnlySequence<byte>? framed;
            try
            {
                framed = await _responseReader.ReadAsync(ct);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "CRI worker closed its output unexpectedly (it likely crashed on startup - "
                    + "check stderr above for a Python traceback)", e);
            }

            if (framed is null)
            {
                throw new InvalidOperationException(
                    "CRI worker closed its output unexpectedly (it likely crashed on startup - "
                    + "check stderr above for a Python traceback)");
            }

            var response = MessagePackSerializer.Deserialize<CriResponse>(framed.Value);

            if (response.Id != request.Id)
            {
                throw new InvalidOperationException(
                    $"CRI worker response id mismatch: sent {request.Id}, got {response.Id}");
            }

            if (!response.Ok)
            {
                throw new InvalidOperationException($"CRI worker error ({request.Op}): {response.Error}");
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort shutdown - the process exiting uncleanly isn't worth failing the run over.
        }

        _responseReader.Dispose();
        _process.Dispose();
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
