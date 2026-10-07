# Sample storage and playback architecture

Heresy treats decoded sample PCM as live song state. File/archive formats are
persistence and import concerns; realtime rendering must not perform filesystem,
ZIP, or audio-codec work.

## Live sample state

Every playable sample in a loaded authoring document owns an immutable decoded
PCM representation in memory. Playback snapshots carry that PCM state with the
sample, and render-time source resolution uses it directly.

A sample may also have one of two encoded-source states:

1. **Persisted encoded identity** — a path/location belonging to the current
   saved song plus the recorded content signature. The encoded bytes are not
   retained in memory. This identity exists so a later save can reuse the
   already-persisted encoded representation after verifying that its signature
   still matches.
2. **Pending encoded payload** — immutable encoded bytes plus their identity
   metadata. This is used for a sample imported from outside the current song
   (for example a flat audio file or a sample copied from another Heresy
   module). The original external location is not a dependency of the imported
   sample.

These states are mutually exclusive. Decoded PCM is present in both.

## Loading

Loading a song resolves each sample's encoded data while the document is being
loaded. Archive entries are opened and compressed audio formats are decoded at
this point. Once loading completes, playback no longer needs the archive,
external file, or codec.

For a loaded sample, only the persisted encoded identity/signature and decoded
PCM remain in the live document. The encoded bytes themselves are discarded.

A Heresy module must not create a live cross-module sample dependency. Loading
or importing a sample from another module copies the sample into the current
song; it does not preserve a link back to the other module.

## Importing

Importing an audio file or a sample from another song:

- reads/copies the encoded source while the import operation is running;
- decodes it immediately to plain in-memory PCM;
- retains the encoded bytes as the sample's pending encoded payload until the
  current song has saved its own copy;
- does not require the import source to remain present afterward.

This rule applies especially to compressed formats, where re-encoding the PCM
would not necessarily reproduce the imported representation.

## Saving

Before reusing a persisted encoded representation, saving verifies that the
current song-owned file/archive entry still matches the recorded signature.
Playback never depends on this check; it is solely a persistence-integrity
operation.

For a pending imported payload, save writes the encoded bytes into the current
song's storage. After the save commits successfully, the live sample replaces
the pending bytes with the new persisted encoded identity/signature and releases
the encoded byte buffer. Its state is then equivalent to a sample freshly loaded
from that saved song.

Decoded PCM remains resident throughout saving.

## Realtime boundary

The realtime path may allocate lightweight playback state around already-loaded
PCM, but it must not:

- open sample files;
- open or decompress `.hm` ZIP archives;
- decode WAVE, FLAC, MP3, OGG, AIFF, or any other file format;
- hash persistence files.

Support for additional encoded formats therefore belongs to the load/import
codec layer, not to a "realtime decoder" layer.
