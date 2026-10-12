# ww2ogg-cs

Converts Audiokinetic Wwise RIFF/RIFX Vorbis audio (`.wem`) into standard Ogg Vorbis. This is a **C# rewrite** of [hcs64/ww2ogg](https://github.com/hcs64/ww2ogg) by hcs (C++, v0.24).

It keeps the original Vorbis conversion logic and adds support for Opus WEMs, batch processing of `.pck` / `.bnk` containers, and WAV / MP3 output.

> If you only want to play the audio and do not specifically need Ogg output, the original author recommends [vgmstream](https://github.com/vgmstream/vgmstream). The generated Ogg files can also be post-processed with [revorb](https://hydrogenaud.io/index.php/topic,64328.0.html) to make them smaller and cleaner.

## Comparison with the original

| | original ww2ogg | ww2ogg-cs |
| --- | --- | --- |
| Language / runtime | C++ | C#, `.NET 10` |
| Vorbis WEM to Ogg | yes | yes |
| Opus WEM | no | yes (the embedded Ogg Opus stream is extracted as `.opus`) |
| `.pck` / `.bnk` containers | no | yes (lists and converts every embedded WEM) |
| Output formats | Ogg Vorbis | `.ogg` / `.opus` / `--wav` / `--mp3` |
| Input type detection | manual | automatic, based on file contents (WEM / container) |
| Native dependencies | none | none (Opus decoding uses Concentus, Vorbis decoding uses NVorbis, MP3 encoding uses GroovyCodecs — all fully managed) |

## Usage

```
ww2ogg-cs input.wem [-o output.ogg|output.opus|output.mp3|output.wav] [--inline-codebooks]
                    [--full-setup] [--mod-packets | --no-mod-packets]
                    [--pcb packed_codebooks_aoTuV_603.bin]
ww2ogg-cs input.pck|input.bnk [-o outdir] [--mp3|--wav] [--list] [other options above]
```

Without `-o`: a single audio file is written next to the input with the `.ogg` extension (`.opus` for Opus sources, or the matching extension with `--wav` / `--mp3`); a container is extracted into `<filename>_extracted/`.

Examples:

```bash
# Convert a single WEM to Ogg
ww2ogg-cs 2171dd5893870ccf.wem

# Unpack a whole pck/bnk and convert everything to MP3
ww2ogg-cs 2171dd5893870ccf.pck --mp3 -o out_dir

# Only list the entries of a container, do not convert
ww2ogg-cs 2171dd5893870ccf.pck --list

# If the result sounds wrong, try the other codebooks and no packet modification
ww2ogg-cs input.wem --pcb packed_codebooks_aoTuV_603.bin --no-mod-packets
```

### Options

| Option | Description |
| --- | --- |
| `-o <path>` | Output file name, or output directory in container mode |
| `--mp3` | Decode and write MP3 (128 kbps by default, see `Mp3Converter.DefaultBitRate`) |
| `--wav` | Decode and write WAV (16-bit PCM) |
| `--list` | In container mode, list the entries and exit |
| `--inline-codebooks` | Use the codebooks embedded in the WEM instead of an external `.bin` |
| `--full-setup` | Early format with the setup header almost entirely intact; implies `--inline-codebooks` |
| `--mod-packets` / `--no-mod-packets` | Force whether Vorbis packets are rearranged; mutually exclusive |
| `--pcb <file>` | Packed codebooks file to use, default `packed_codebooks_aoTuV_603.bin` |

Exit code: `0` on success, `1` on failure (bad arguments, file cannot be opened, nothing convertible, etc.).

## Build and run

Requires the `.NET 10 SDK` (`global.json` pins 10.0.401 with `rollForward: latestFeature`).

```bash
cd ww2ogg-cs
dotnet build -c Release

# Running without arguments prints the usage
dotnet bin/Release/net10.0/ww2ogg-cs.dll

# Native AOT publish (PublishAot is enabled in the project)
dotnet publish -c Release -r win-x64 -o publish
```

`packed_codebooks.bin` and `packed_codebooks_aoTuV_603.bin` live in the project directory and are copied to the output directory by the build; at runtime **both files must sit next to the executable** (or be pointed at with `--pcb`).

## Code layout

| File | Purpose |
| --- | --- |
| `Program.cs` | Command line parsing, input type detection, single-file and container flows |
| `WwiseRiffVorbis.cs` | Vorbis WEM (RIFF/RIFX) parsing, generates Ogg Vorbis |
| `WwiseRiffOpus.cs` | Opus WEM detection and embedded Ogg Opus stream extraction |
| `WwiseContainer.cs` | `.pck` / `.bnk` parsing, lists embedded WEM entries |
| `CodebookLibrary.cs` | Tremor lowmem style codebook reconstruction, shareable across conversions |
| `BitIo.cs` | Bit-level I/O and Ogg page framing (`BitReader` / `BitWriter`) |
| `OpusConverter.cs` | Opus to 16-bit PCM (Concentus) |
| `WavConverter.cs` | Ogg to WAV, PCM to WAV |
| `Mp3Converter.cs` | Ogg / PCM to MP3 (GroovyCodecs) |
| `Crc32.cs` | Ogg page CRC32 |
| `Errors.cs` | `ArgumentError` / `FileOpenError` / `ParseError` and friends |
| `AppVersion.cs` | Reads the version from the assembly metadata |

The public, embeddable surface is `WwiseRiffVorbis`, `WwiseRiffOpus`, `CodebookLibrary` (shared codebooks) and the static helpers in `OpusConverter` / `WavConverter` / `Mp3Converter`; the rest of the parsers are `internal`.

## Version

The version is defined in a single place, `<Version>` in `ww2ogg-cs.csproj`. `AppVersion` reads it back and exposes it in the startup banner and in the Vorbis vendor string of converted files.

## License and credits

The original [hcs64/ww2ogg](https://github.com/hcs64/ww2ogg) was written by hcs and is licensed under BSD-3-Clause (see `COPYING` upstream); this C# rewrite follows the same license.

Third-party libraries: Concentus, NVorbis, GroovyCodecs.Mp3.
