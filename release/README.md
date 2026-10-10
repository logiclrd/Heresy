# Heresy — music tracker

Heresy is a desktop music tracker for Windows, Linux and macOS.
Arrange notes in Patterns and Sequences, work with sampled and FM-synthesized
sounds, edit Instruments and Envelopes, and export finished music.
For more advanced compositions, Patterns, Sequences, Instruments,
Envelopes and certain effects can use a restricted C# expression or script.

## Start Heresy

1. Extract the **entire** downloaded archive into a folder. Keep all
   the DLLs, native libraries and runtime files together; do not move
   only the executable.
2. Start the application:
   - **Windows:** double-click `Heresy.exe` inside the `Heresy` folder.
   - **Linux:** open a terminal in the extracted `Heresy` folder
     and run `./Heresy`.
   - **macOS:** open Terminal in the extracted `Heresy` folder
     and run `./Heresy` (the archive contains a command-line-launchable
     desktop executable, not a macOS `.app` bundle).
3. The archive is **self-contained**: no separate .NET installation
   is required. Your operating system, graphics/display environment
   and audio devices must still support the bundled native libraries.

Download the archive whose filename matches your system's architecture:
`x64` for Intel/AMD 64-bit, or `arm64` for ARM64 (including
Apple Silicon). Windows archives are ZIP files, Linux archives are
`.tar.bz2`, and macOS archives are ZIP files. On Linux, if extraction
did not preserve the executable bit, run `chmod +x Heresy` inside
the extracted folder before starting it.

These packages are not asserted to be code-signed or notarized. Your
operating system may display a security warning for an unsigned app;
verify that the package comes from this project's GitHub release
before deciding whether to run it.

## Start making music

The main window groups song objects into five panes:

- **Sequences**: arrange Patterns into the song's order of playback.
- **Patterns**: write notes and effect commands in tracker-style rows.
- **Patches**: import audio Samples or create FM synthesizer patches.
- **Envelopes**: shape changes over a note's lifetime, including releases.
- **Instruments**: map pitches to Patches and Envelopes; table-driven
  and scripted Instruments are available.

Start by importing a Sample or creating an FM Patch, then create a
Pattern with a note referring to that source. Create a Sequence
containing the Pattern to form an arrangement. Use the application's
playback controls to audition your work. Pattern effect cells can hold
multiple effects. Scripts are optional: basic songs do not require C#.

Heresy can import audio in WAV, FLAC, MP3, OGG Vorbis and AIFF/AIFC
formats. Audio is decoded when loaded so playback does not depend
on the original external sample file.

## Save, open and export

Save a song as `.hm` (one portable Heresy module containing the
song manifest and included audio assets) or `.hm.json` (readable
JSON referencing audio assets). The latter may rely on paths
outside the JSON file; keep those source assets available when
sharing that format. Use **File → Open** to open existing songs.
If an asset is missing, Heresy can offer to locate a replacement.

Use **File → Render Audio** to export FLAC (the default), WAV or MP3.
WAV supports selectable 8-, 16-, 24- and 32-bit integer PCM.
MP3 export is restricted to mono/stereo and supported sample rates;
FLAC and WAV support the available multichannel layouts subject to
their encoding rules.

Use **Options → Audio Output** to select an output sample rate,
mono/stereo/5.1/7.1 speaker layout, and per-speaker output settings.
Physical surround-speaker mapping can depend on your audio device.
A visible **Audio underruns** count indicates that the audio
rendering worker failed to supply samples quickly enough.

## Troubleshooting

- **The application does not start:** Confirm the archive matches
  your operating system and CPU architecture and that the whole
  `Heresy` directory was extracted. Launch from a terminal to
  view any startup errors.
- **No audio:** Check the operating system's audio device selection
  and the **Options → Audio Output** settings.
- **A song reports missing Samples:** Use the asset-recovery
  prompts when opening it, or ask the person who shared the song
  for a packaged `.hm` module.
- **An export is rejected:** Verify that your selected format supports
  the current channel count and sample rate; MP3 supports only
  mono or stereo.

## Build identification and source

`BUILD-COMMIT.txt` in this directory contains the **full Git commit
hash** used to build this exact archive. The release filename and
embedded .NET assembly metadata identify the release version.
The project source, issue tracker and development documentation are at
https://github.com/logiclrd/Heresy .
