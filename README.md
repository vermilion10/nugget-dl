# nugget-dl

A CLI tool for downloading and extracting a certain Unity game's asset bundles directly from the
game's CDN, no local game install required.

This is an unofficial, fan-made tool, not affiliated with the game's developers or publishers.
It exists for asset research, dataset building, and archival purposes. See
[Disclaimer](#disclaimer) below.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), to build and run the CLI.
- [uv](https://docs.astral.sh/uv/), only needed if you extract `.acb`/`.awb` (audio) or
  `.usm` (video) bundles. `uv` transparently resolves and caches the small isolated Python
  worker's dependencies (see [Architecture](#architecture)) the first time it runs; no manual
  `pip install` step. Not needed for `.ab` bundles (images/text) or `spine` extraction.
- [ffmpeg](https://ffmpeg.org/download.html) on `PATH`, only needed for `.usm` video
  extraction (muxes the demuxed streams into MP4/HLS). Any recent build with libx264 works;
  NVENC is used automatically if available, detected at runtime.

## Setup

nugget-dl doesn't include the game's server addresses or keys. Before the first run, copy
`client.example.json` to `%APPDATA%\nugget-dl\client.json` (or next to the executable) and fill in:

- `patchCdns` and `launchers`: the patch and PC launcher CDNs for each preset;
- `bundleKeys`: the UnityCN bundle keys and the client version each applies from;
- `audioKey`: the HCA audio key;
- `textKey`: the XOR table for obfuscated text assets, as hex;
- `dataFolder`: the install's data folder name, for `--game-dir`.

Without this file every source reports that it is missing.

## Building

```bash
dotnet build NuggetDl.sln
```

Run the CLI directly with `dotnet run`, or build once and use the produced executable:

```bash
dotnet run --project src/NuggetDl.Cli -- list --preset global
# or
dotnet build NuggetDl.sln
./src/NuggetDl.Cli/bin/Debug/net8.0/NuggetDl.Cli.exe list --preset global
```

## Usage

Three commands: `list`, `extract`, `spine`. All three share a set of source-selection options
that decide which game server/version to pull from.

### Source selection (shared by all commands)

| Option | Description |
|---|---|
| `--preset <name>` | `global`, `korea`, `japan`, `taiwan`, `china`, `china-beta`. Sets both primary and patch source. Defaults to `global` if nothing else is given. |
| `--primary <type>` | Primary source: `obb`, `local`, or a PC starter CDN (`EN_PC`, `KR_PC`, `JP_PC`, `TW_PC`, `CN_PC`, `CN_PC_BETA`). |
| `--obb <path>` | Path to an `.obb` file. Only valid with `--primary obb`. |
| `--game-dir <path>` | Path to an installed game directory. Only valid with `--primary local`. |
| `--patch <type>` | Patch CDN source, e.g. `EN_PC`. |
| `--version <x.y.z>` | Client version to target. Inferred from the primary source if omitted. |
| `--prerelease` | Use the prerelease patch source, if one exists. |

The `china-beta` preset additionally requires a `PATCH_SIGN_KEY` environment variable (its CDN
requires signed requests; this isn't a value the tool can derive on its own).

### `list`

Prints every bundle name the resolved source set can see. Extra positional arguments are
case-insensitive substring filters, AND-combined, with matches highlighted (grep-style) when
output isn't redirected.

```bash
# Every bundle on the global server
dotnet run --project src/NuggetDl.Cli -- list --preset global

# Bundles whose path contains both "spine" and "sailika"
dotnet run --project src/NuggetDl.Cli -- list --preset global spine sailika
```

### `extract`

Downloads, decrypts, and converts bundles to disk. Takes explicit bundle names, or a selection
flag:

| Option | Selects |
|---|---|
| `--pattern <substring>` | Bundles whose path contains this substring (case-insensitive); repeatable, AND-combined with each other and with explicit bundle names. |
| `--all` | Every bundle. |
| `--all-temp` | Every `.ab` bundle under `assets/temp/` (text/table data). |
| `--all-images` | Every `.ab` bundle under `assets/product/texture/`. |
| `--all-audio` | Every `.acb` bundle. |
| `--all-video` | Every `.usm` bundle. |
| `--hls` | Also emit an HLS variant playlist for each video, alongside the MP4. |

```bash
# One bundle by exact name (discover names with `list`)
dotnet run --project src/NuggetDl.Cli -- extract \
  assets/temp/bytes/share/weekchallenge.ab \
  --preset global --output ./out

# Everything matching a pattern
dotnet run --project src/NuggetDl.Cli -- extract \
  --pattern uicommon --pattern itemsicon --preset global --output ./out

# Every image bundle
dotnet run --project src/NuggetDl.Cli -- extract --all-images --preset global --output ./out

# A video, with HLS as well as MP4
dotnet run --project src/NuggetDl.Cli -- extract \
  External/Video/BountyClg31036.usm --hls --preset global --output ./out
```

**Output layout**, each bundle's contents are mirrored under `--output`, converting as they go:

- **Images** → `.png` and `.webp`. Character art under `image/rolecharacter/` or
  `image/rolestory/` additionally gets a 256px `.256.webp` thumbnail. Sprites packed into an
  atlas (both directly and via a `SpriteAtlas` asset) are cropped and rotation/flip-corrected;
  tight-packed sprites get the correct rectangular crop but not the polygon silhouette trim.
- **Text** → decoded in place (the `.bytes` suffix is dropped, obfuscated `.lua` is decrypted).
  Binary tables under `assets/temp/bytes/` are always converted to `.csv` (there's no
  `--convert-binary-tables` opt-out yet).
- **Audio** (`.acb`/`.awb`) → `audio/<name>/<cue>.wav`. One WAV per named cue in the ACB
- **Video** (`.usm`) → `video/<name>.mp4` (H.264 + MP3, faststart, one audio track per language
  with a `language` tag), plus `video/streams/<name>/` with an HLS master playlist and segments
  if `--hls` was passed.

### `spine`

Reconstructs Spine2D rigs (atlas + skeleton JSON + textures) from the game's spine bundles.

```bash
dotnet run --project src/NuggetDl.Cli -- spine \
  assets/product/ui/spine/sailika.ab --preset global --output ./out
```

A spine is frequently split across several bundles, one holding the prefab, others holding
the skeleton data/atlas/textures it references via cross-bundle Unity `PPtr`s (e.g.
`2-17spine/qishitutorial.ab` referencing `2-17spine/qishitutorial/qishitutorial_bg.ab` and
`.../qishitutorial_role.ab`). `spine` finds and loads those siblings automatically, passing
`foo.ab` also loads every bundle under `foo/`. Output is written per prefab found, as
`<output>/<prefab-name>/<prefab-name>/{index.json,*.atlas,*.json,*.png}`.

## Architecture

```
NuggetDl/
├── src/
│   ├── NuggetDl.Core/          # Domain logic: sources/CDN, binary tables, spine models,
│   │                            # audio cue lookup, ffmpeg muxing. No Unity-parsing dependency.
│   ├── NuggetDl.UnityInterop/  # AssetsTools.NET-backed bundle/spine/texture reading,
│   │                            # UnityCN decryption.
│   ├── NuggetDl.CriInterop/    # Talks msgpack over stdio to tools/cri_worker.
│   └── NuggetDl.Cli/           # The `nugget-dl` command-line entry point.
└── tools/
    └── cri_worker/              # Isolated Python worker: ACB/AWB/HCA/USM codec handling via
                                  # PyCriCodecsEx, which has no maintained C# equivalent. Talks
                                  # msgpack over stdin/stdout to NuggetDl.CriInterop; run
                                  # directly with `uv run worker.py` (self-installs its
                                  # dependencies via inline PEP 723 metadata). Stateless per
                                  # request, only decoded track bytes cross the boundary, all
                                  # file I/O and bundle resolution stays in C#.
```

Everything except the CRI worker is C#. The worker is a deliberate, narrow exception: the game's
audio/video is packed in Criware's ADX/HCA/USM formats, and the only actively maintained
library that understands them is Python's `PyCriCodecsEx`, there's no equivalent C# package,
only unmaintained internals of other tools. Rather than vendor and maintain a fork of that,
the codec-specific part stays in an isolated subprocess; C# does everything else (bundle
resolution, file I/O, CLI, image/video muxing).

## Disclaimer

This tool downloads and decrypts assets from the game's CDN. The repository contains no game
assets, server addresses or keys; you supply those in `client.json`. Nothing extracted or produced by
this tool should be redistributed in ways that infringe the game owners'
copyright; use it for research, personal archival, and dataset-building, and respect the game's
terms of service.

## Acknowledgements

- [huaxu-app/pgr-assets](https://github.com/huaxu-app/pgr-assets)
- [hengtek/PGRStudio](https://github.com/hengtek/PGRStudio)