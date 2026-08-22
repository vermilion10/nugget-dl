using NuggetDl.Core;
using NuggetDl.Core.Audio;
using NuggetDl.Core.Extraction;
using NuggetDl.Core.Sources;
using NuggetDl.Core.Video;
using NuggetDl.CriInterop;
using NuggetDl.UnityInterop;
using System.CommandLine;

namespace NuggetDl.Cli;

/// <summary>
/// Targets System.CommandLine 3.0.0-preview.7, whose API differs a lot from
/// the older 2.0.0-betas (SetAction instead of SetHandler, Options.Add /
/// Arguments.Add instead of collection-initializer add, Required instead of
/// IsRequired, Parse(args).Invoke() instead of InvokeAsync(args) directly on
/// the command). Confirmed working against this exact API in the last build.
///
/// `list` now goes through SourceSetBuilder: --preset defaults to "global" when nothing else is
/// given, or --primary/--patch/--obb/--game-dir can be set explicitly.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var patternsArgument = new Argument<string[]>("patterns")
        {
            Description = "Only print bundles containing all of these (case-insensitive substring)",
        };

        var presetOption = new Option<string?>("--preset")
        {
            Description = "Preset to use for primary and patch: global, korea, japan, taiwan, china, china-beta",
        };

        var primaryOption = new Option<string?>("--primary")
        {
            Description = "Primary source to use: obb, local, EN_PC, KR_PC, JP_PC, TW_PC, CN_PC, CN_PC_BETA",
        };

        var obbOption = new Option<string?>("--obb")
        {
            Description = "Path to obb file. Only valid when --primary is 'obb'",
        };

        var gameDirOption = new Option<string?>("--game-dir")
        {
            Description = "Path to an installed game directory. Only valid when --primary is 'local'",
        };

        var patchOption = new Option<string?>("--patch")
        {
            Description = "Patch source to use, e.g. EN_PC",
        };

        var versionOption = new Option<string?>("--version")
        {
            Description = "The client version to use, e.g. 4.5.0. Inferred by default",
        };

        var prereleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Use the prerelease patch source, if available",
        };

        var listCommand = new Command("list", "List all available bundles");
        listCommand.Arguments.Add(patternsArgument);
        listCommand.Options.Add(presetOption);
        listCommand.Options.Add(primaryOption);
        listCommand.Options.Add(obbOption);
        listCommand.Options.Add(gameDirOption);
        listCommand.Options.Add(patchOption);
        listCommand.Options.Add(versionOption);
        listCommand.Options.Add(prereleaseOption);

        listCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new SourceSetOptions(
                Preset: parseResult.GetValue(presetOption),
                Prerelease: parseResult.GetValue(prereleaseOption),
                Primary: parseResult.GetValue(primaryOption),
                Obb: parseResult.GetValue(obbOption),
                GameDir: parseResult.GetValue(gameDirOption),
                Patch: parseResult.GetValue(patchOption),
                Version: parseResult.GetValue(versionOption));

            var patterns = parseResult.GetValue(patternsArgument) ?? [];
            await RunListAsync(patterns, options, cancellationToken);
            return 0;
        });

        var bundlesArgument = new Argument<string[]>("bundles")
        {
            Description = "Exact bundle names to extract, e.g. assets/temp/bytes/share/weekchallenge.ab",
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write extracted files to",
            Required = true,
        };

        var patternOption = new Option<string[]>("--pattern")
        {
            Description = "Also select bundles containing all of these (case-insensitive substring); repeatable",
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Select every bundle the source set can list",
        };

        var allTempOption = new Option<bool>("--all-temp")
        {
            Description = "Select every temp (text) bundle",
        };

        var allImagesOption = new Option<bool>("--all-images")
        {
            Description = "Select every texture bundle",
        };

        var allAudioOption = new Option<bool>("--all-audio")
        {
            Description = "Select every audio (.acb) bundle",
        };

        var allVideoOption = new Option<bool>("--all-video")
        {
            Description = "Select every video (.usm) bundle",
        };

        var hlsOption = new Option<bool>("--hls")
        {
            Description = "Also generate an HLS variant playlist for each video, alongside the MP4",
        };

        var extractCommand = new Command("extract", "Extract one or more bundles to disk");
        extractCommand.Arguments.Add(bundlesArgument);
        extractCommand.Options.Add(outputOption);
        extractCommand.Options.Add(patternOption);
        extractCommand.Options.Add(allOption);
        extractCommand.Options.Add(allTempOption);
        extractCommand.Options.Add(allImagesOption);
        extractCommand.Options.Add(allAudioOption);
        extractCommand.Options.Add(allVideoOption);
        extractCommand.Options.Add(hlsOption);
        extractCommand.Options.Add(presetOption);
        extractCommand.Options.Add(primaryOption);
        extractCommand.Options.Add(obbOption);
        extractCommand.Options.Add(gameDirOption);
        extractCommand.Options.Add(patchOption);
        extractCommand.Options.Add(versionOption);
        extractCommand.Options.Add(prereleaseOption);

        extractCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new SourceSetOptions(
                Preset: parseResult.GetValue(presetOption),
                Prerelease: parseResult.GetValue(prereleaseOption),
                Primary: parseResult.GetValue(primaryOption),
                Obb: parseResult.GetValue(obbOption),
                GameDir: parseResult.GetValue(gameDirOption),
                Patch: parseResult.GetValue(patchOption),
                Version: parseResult.GetValue(versionOption));

            var selection = new BundleSelection(
                Explicit: parseResult.GetValue(bundlesArgument) ?? [],
                Patterns: parseResult.GetValue(patternOption) ?? [],
                All: parseResult.GetValue(allOption),
                AllTemp: parseResult.GetValue(allTempOption),
                AllImages: parseResult.GetValue(allImagesOption),
                AllAudio: parseResult.GetValue(allAudioOption),
                AllVideo: parseResult.GetValue(allVideoOption));

            var output = parseResult.GetValue(outputOption)!;
            var hls = parseResult.GetValue(hlsOption);
            return await RunExtractAsync(selection, output, hls, options, cancellationToken);
        });

        var root = new RootCommand("nugget-dl");
        root.Subcommands.Add(listCommand);
        root.Subcommands.Add(extractCommand);

        var spineBundlesArgument = new Argument<string[]>("bundles")
        {
            Description = "Bundle names to extract Spine skeletons from",
        };

        var spineCommand = new Command("spine", "Extract Spine skeletons from one or more bundles");
        spineCommand.Arguments.Add(spineBundlesArgument);
        spineCommand.Options.Add(outputOption);
        spineCommand.Options.Add(presetOption);
        spineCommand.Options.Add(primaryOption);
        spineCommand.Options.Add(obbOption);
        spineCommand.Options.Add(gameDirOption);
        spineCommand.Options.Add(patchOption);
        spineCommand.Options.Add(versionOption);
        spineCommand.Options.Add(prereleaseOption);

        spineCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new SourceSetOptions(
                Preset: parseResult.GetValue(presetOption),
                Prerelease: parseResult.GetValue(prereleaseOption),
                Primary: parseResult.GetValue(primaryOption),
                Obb: parseResult.GetValue(obbOption),
                GameDir: parseResult.GetValue(gameDirOption),
                Patch: parseResult.GetValue(patchOption),
                Version: parseResult.GetValue(versionOption));

            var bundles = parseResult.GetValue(spineBundlesArgument) ?? [];
            var output = parseResult.GetValue(outputOption)!;
            await RunSpineAsync(bundles, output, options, cancellationToken);
            return 0;
        });

        root.Subcommands.Add(spineCommand);

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Extracts each requested bundle together with its related bundles, so
    /// spines split across several .ab files combine correctly (see
    /// SpineCliBridge.ExtractGroup for why that grouping is required).
    /// Related bundles are found by name: the game packs a split spine as
    /// `<name>.ab` plus `<name>/<part>.ab`, so everything under the bundle's
    /// own directory prefix is loaded alongside it. Passing several bundles
    /// explicitly still works - they're grouped by prefix, and any extra
    /// bundle named on the command line joins the group it belongs to.
    /// </summary>
    private static async Task RunSpineAsync(
        string[] bundles, string outputDir, SourceSetOptions options, CancellationToken ct)
    {
        var bundleIndexReader = new UnityBundleIndexReader();
        var resolved = await SourceSetBuilder.BuildAsync(options, bundleIndexReader, ct);

        var decryptKeyBytes = string.IsNullOrEmpty(resolved.DecryptKey)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(resolved.DecryptKey);

        var allBundles = await resolved.Sources.ListAllBundlesAsync(ct);
        var explicitlyRequested = new HashSet<string>(bundles, StringComparer.OrdinalIgnoreCase);

        foreach (var bundle in bundles)
        {
            try
            {
                var group = new List<string> { bundle };

                // `foo.ab` -> everything under `foo/`, which is where the game puts
                // the skeleton-data halves of a split spine.
                if (bundle.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = bundle[..^".ab".Length] + "/";
                    group.AddRange(allBundles
                        .Where(b => b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && b.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)
                            && !explicitlyRequested.Contains(b))
                        .OrderBy(b => b, StringComparer.Ordinal));
                }

                if (group.Count > 1)
                {
                    Console.Error.WriteLine(
                        $"{bundle}: loading {group.Count - 1} related bundle(s) for cross-bundle references");
                }

                var fetched = new List<(string, byte[])>();
                foreach (var member in group)
                {
                    fetched.Add((member, await resolved.Sources.FindBundleAsync(member, ct)));
                }

                SpineCliBridge.ExtractGroup(fetched, decryptKeyBytes, outputDir, resolved.Version);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to extract spine from {bundle}: {e.Message}");
            }
        }
    }

    /// <summary>Bundle-selection flags shared by commands that operate on a
    /// chosen set of bundles.</summary>
    private sealed record BundleSelection(
        string[] Explicit, string[] Patterns, bool All, bool AllTemp, bool AllImages, bool AllAudio, bool AllVideo)
    {
        public bool HasAnyFilter => All || AllTemp || AllImages || AllAudio || AllVideo || Patterns.Length > 0;
    }

    /// <summary>Resolves a BundleSelection into the deduplicated, sorted set
    /// of bundle names to act on.</summary>
    private static async Task<List<string>> ResolveSelectionAsync(BundleSelection selection, SourceSet sources, CancellationToken ct)
    {
        var selected = new HashSet<string>(selection.Explicit, StringComparer.Ordinal);

        if (selection.HasAnyFilter)
        {
            var allBundles = await sources.ListAllBundlesAsync(ct);

            foreach (var bundle in allBundles)
            {
                var isAb = bundle.EndsWith(".ab", StringComparison.OrdinalIgnoreCase);
                if (selection.All
                    || (selection.AllTemp && isAb && bundle.Contains(AssetPaths.TempBundleMarker, StringComparison.Ordinal))
                    || (selection.AllImages && isAb && bundle.Contains(AssetPaths.TextureBundleMarker, StringComparison.Ordinal))
                    || (selection.AllAudio && bundle.EndsWith(".acb", StringComparison.OrdinalIgnoreCase))
                    || (selection.AllVideo && bundle.EndsWith(".usm", StringComparison.OrdinalIgnoreCase)))
                {
                    selected.Add(bundle);
                }
            }

            if (selection.Patterns.Length > 0)
            {
                foreach (var bundle in FilterBundles(allBundles, selection.Patterns))
                {
                    selected.Add(bundle);
                }
            }
        }

        return selected.OrderBy(b => b, StringComparer.Ordinal).ToList();
    }

    private static async Task<int> RunExtractAsync(
        BundleSelection selection, string outputDir, bool hls, SourceSetOptions options, CancellationToken ct)
    {
        var bundleIndexReader = new UnityBundleIndexReader();
        var resolved = await SourceSetBuilder.BuildAsync(options, bundleIndexReader, ct);

        var bundles = await ResolveSelectionAsync(selection, resolved.Sources, ct);
        if (bundles.Count == 0)
        {
            Console.Error.WriteLine("No bundles specified");
            return 1;
        }

        // Separate concrete reader from the one SourceSetBuilder configured -
        // its own decrypt key needs setting independently.
        var contentReader = new UnityBundleContentReader();
        contentReader.SetDecryptKey(resolved.DecryptKey);

        // The CRI worker (audio/.acb/.awb, video/.usm demux) is only started
        // if something in this batch actually needs it - a plain .ab extract
        // shouldn't pay for spinning up uv/Python at all.
        var needsCri = bundles.Any(b =>
            b.EndsWith(".acb", StringComparison.OrdinalIgnoreCase) || b.EndsWith(".usm", StringComparison.OrdinalIgnoreCase));
        var needsVideo = bundles.Any(b => b.EndsWith(".usm", StringComparison.OrdinalIgnoreCase));

        CriWorkerClient? cri = null;
        string? criStartError = null;
        CueRegistry? cues = null;
        List<IVideoEncoder> videoEncoders = [new Mp4Encoder()];
        if (hls)
        {
            videoEncoders.Add(new HlsEncoder());
        }

        if (needsCri)
        {
            try
            {
                cri = CriWorkerClient.Start();
            }
            catch (Exception e)
            {
                criStartError = e.Message;
            }

            if (cri is not null && bundles.Any(b => b.EndsWith(".acb", StringComparison.OrdinalIgnoreCase)))
            {
                cues = new CueRegistry();
                try
                {
                    await cues.InitAsync(resolved.Sources, contentReader, resolved.Version, ct);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(
                        $"Failed to load the audio cue sheet, falling back to per-bundle .awb lookup: {e.Message}");
                    cues = null;
                }
            }
        }

        if (needsVideo)
        {
            foreach (var encoder in videoEncoders)
            {
                await encoder.SetupAsync(ct);
            }
        }

        var failCount = 0;

        try
        {
            foreach (var bundle in bundles)
            {
                if (bundle.EndsWith(".awb", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // pulled in alongside its .acb, not extracted on its own
                }

                try
                {
                    if (bundle.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
                    {
                        var bundleBytes = await resolved.Sources.FindBundleAsync(bundle, ct);
                        var assets = contentReader.LoadContents(bundleBytes);
                        BundleExtractor.ExtractBundle(
                            assets,
                            outputDir,
                            resolved.Version,
                            onExtracted: path => Console.WriteLine($"Extracted {path}"));
                    }
                    else if (bundle.EndsWith(".acb", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cri is null)
                        {
                            throw new InvalidOperationException($"CRI worker unavailable: {criStartError}");
                        }

                        await ExtractAudioAsync(bundle, resolved.Sources, outputDir, cri, cues, ct);
                    }
                    else if (bundle.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cri is null)
                        {
                            throw new InvalidOperationException($"CRI worker unavailable: {criStartError}");
                        }

                        await ExtractUsmAsync(bundle, resolved.Sources, outputDir, cri, videoEncoders, ct);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Skipping {bundle}: unsupported bundle type");
                        failCount++;
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Failed to extract {bundle}: {e.Message}");
                    failCount++;
                }
            }
        }
        finally
        {
            if (cri is not null)
            {
                await cri.DisposeAsync();
            }
        }

        if (failCount > 0)
        {
            Console.Error.WriteLine($"{failCount} of {bundles.Count} bundle(s) failed");
        }

        return failCount > 0 ? 1 : 0;
    }

    /// <summary>Resolves an .acb's companion .awb (via the cue sheet if one
    /// was loaded, otherwise a same-name sibling lookup), decodes every cue's
    /// waveform through the CRI worker, and writes each as a .wav.</summary>
    private static async Task ExtractAudioAsync(
        string bundle, SourceSet sources, string outputDir, CriWorkerClient cri, CueRegistry? cues, CancellationToken ct)
    {
        var cueSheet = cues?.GetCueSheet(bundle);

        string baseName;
        byte[] acbData;
        byte[]? awbData = null;

        if (cueSheet is not null)
        {
            baseName = cueSheet.BaseName;
            acbData = await sources.FindBundleAsync(cueSheet.Acb, ct);
            if (!string.IsNullOrEmpty(cueSheet.Awb))
            {
                awbData = await sources.FindBundleAsync(cueSheet.Awb, ct);
            }
        }
        else
        {
            baseName = CueSheetEntry.DeriveBaseName(bundle);
            acbData = await sources.FindBundleAsync(bundle, ct);
            try
            {
                awbData = await sources.FindBundleAsync(bundle.Replace(".acb", ".awb", StringComparison.Ordinal), ct);
            }
            catch (BlobNotFoundException)
            {
                // AWB is optional - the ACB may carry its waveforms inline.
            }
        }

        var tracks = await cri.DecodeAcbAsync(acbData, awbData, AudioConstants.AudioKey, ct);
        var dir = Path.Combine(outputDir, "audio", baseName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);

        foreach (var track in tracks)
        {
            var path = Path.Combine(dir, track.Name + ".wav");
            await File.WriteAllBytesAsync(path, track.Wav, ct);
            Console.WriteLine($"Extracted {path}");
        }
    }

    /// <summary>Demuxes a USM's video/audio streams through the CRI worker,
    /// writes them to a scratch temp dir, then muxes them into an MP4 (and,
    /// with --hls, an HLS variant playlist alongside it) via ffmpeg.</summary>
    private static async Task ExtractUsmAsync(
        string bundle, SourceSet sources, string outputDir, CriWorkerClient cri,
        IReadOnlyList<IVideoEncoder> videoEncoders, CancellationToken ct)
    {
        var baseName = CueSheetEntry.DeriveBaseName(bundle);
        var data = await sources.FindBundleAsync(bundle, ct);
        var demuxed = await cri.DemuxUsmAsync(data, AudioConstants.AudioKey, ct);

        var tempDir = Directory.CreateTempSubdirectory("nuggetdl-usm-");
        try
        {
            var videoTracks = new List<VideoTrack>();
            var audioTracks = new List<VideoTrack>();

            foreach (var track in demuxed)
            {
                // No extension needed - ffmpeg sniffs the elementary
                // stream/WAV content directly, matching the Python side's
                // own unextended tempfile names.
                var path = Path.Combine(tempDir.FullName, track.Name.TrimStart('@'));
                await File.WriteAllBytesAsync(path, track.Data, ct);

                switch (track.Kind)
                {
                    case "audio":
                        audioTracks.Add(new VideoTrack(track.Name, path, track.Language));
                        break;
                    case "video":
                        videoTracks.Add(new VideoTrack(track.Name, path));
                        break;
                }
            }

            if (videoTracks.Count == 0)
            {
                Console.Error.WriteLine($"{bundle}: no video track in demuxed output, nothing to encode");
                return;
            }

            var baseOutputPath = Path.Combine(outputDir, "video", baseName.Replace('/', Path.DirectorySeparatorChar));

            foreach (var encoder in videoEncoders)
            {
                await encoder.EncodeAsync(baseOutputPath, videoTracks, audioTracks, ct);
            }

            Console.WriteLine($"Extracted {baseOutputPath}.mp4");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task RunListAsync(string[] patterns, SourceSetOptions options, CancellationToken ct)
    {
        var bundleIndexReader = new UnityBundleIndexReader();
        var resolved = await SourceSetBuilder.BuildAsync(options, bundleIndexReader, ct);

        var bundles = await resolved.Sources.ListAllBundlesAsync(ct);

        var useColor = patterns.Length > 0
            && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        foreach (var bundle in FilterBundles(bundles, patterns))
        {
            Console.WriteLine(useColor ? Highlight(bundle, patterns) : bundle);
        }
    }

    /// <summary>Sorts bundles; with patterns, keeps those matching every one
    /// as a case-insensitive substring (AND-combined).</summary>
    private static List<string> FilterBundles(IEnumerable<string> bundles, string[] patterns)
    {
        var ordered = bundles.OrderBy(b => b, StringComparer.Ordinal).ToList();
        if (patterns.Length == 0)
        {
            return ordered;
        }

        var needles = patterns.Select(p => p.ToLowerInvariant()).ToArray();
        return ordered
            .Where(b => needles.All(n => b.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private const string HighlightStart = "\u001b[01;31m";
    private const string HighlightReset = "\u001b[0m";

    /// <summary>Wraps every case-insensitive match of any pattern in ANSI
    /// color, grep-style, merging overlaps.</summary>
    private static string Highlight(string text, string[] patterns)
    {
        var lowered = text.ToLowerInvariant();
        var spans = new List<(int Start, int End)>();

        foreach (var pattern in patterns)
        {
            var needle = pattern.ToLowerInvariant();
            if (needle.Length == 0)
            {
                continue;
            }

            var start = lowered.IndexOf(needle, StringComparison.Ordinal);
            while (start >= 0)
            {
                spans.Add((start, start + needle.Length));
                start = lowered.IndexOf(needle, start + needle.Length, StringComparison.Ordinal);
            }
        }

        if (spans.Count == 0)
        {
            return text;
        }

        spans.Sort();
        var merged = new List<(int Start, int End)> { spans[0] };
        foreach (var (s, e) in spans.Skip(1))
        {
            var last = merged[^1];
            if (s <= last.End)
            {
                merged[^1] = (last.Start, Math.Max(last.End, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        var result = new System.Text.StringBuilder();
        var prev = 0;
        foreach (var (s, e) in merged)
        {
            result.Append(text.AsSpan(prev, s - prev));
            result.Append(HighlightStart).Append(text.AsSpan(s, e - s)).Append(HighlightReset);
            prev = e;
        }

        result.Append(text.AsSpan(prev));
        return result.ToString();
    }
}
