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

## Native PCM rate and musical tuning

Decoded `SamplePcmData.SampleRate` is the **functional native sample
frame rate**. `SampleSound` calculates source-frame advance from native
sample rate divided by the output sample rate, multiplied by the note's
pitch and playback-speed multipliers and its live pitch trajectory.
Changing the native sample rate (or deliberately tuning a sample through
its source audio) therefore changes the sound at an otherwise identical
note invocation. A musically authored sample at unity pitch/speed
is conventionally tuned to C-4 (261.6255653005986 Hz), the same
universal reference used by FM, Instrument and recursive sounds.

`SampleDefinition.ReferenceFrequencyHz` was a disconnected, unused
property and **has been removed**; it never influenced the `SampleSound`
resampler. Its former “Reference frequency (Hz)” editor field was equally
ineffective and has been removed, while the editor's loop controls
remain functional. New version-one `.hm.json` and `.hm` manifests
omit the old `referenceFrequencyHz` key. Loading preexisting version-one
documents with that key remains supported: its value is ignored, and
saving the document drops it. There is no schema-version change or
source-specific musical reference-frequency API.

## Instrument-selected samples and Note Off

A bare `SampleSound` retains the established direct-sample rule:
Note Off terminates its PCM at the sample's next eligible output frame.
However, when an Instrument selects a sample **and provides an
effective volume envelope**, Note Off must start the envelope's
release **without cutting the sample source early**. The terminal
`SampleSoundState.ReleaseIsControlledByVolumeEnvelope` flag is
invocation-local, established during `InstrumentSound.CreateInvocation`
after the tone's envelope overlay resolves. It prevents
`SampleSound.GetEndFrameExclusive` from treating `NoteOffTime` as
the sample's terminal frame. The Note Off timestamp still reaches
the sound state, and `PlaybackVoice` controls the actual end from
its active volume envelope, including pauses/resumes and repeated
Off being idempotent.

The policy follows recursive Instrument tone selection. An outer
Instrument that overlays a volume envelope can enable a release for
a terminal sample selected through an inner Instrument, while an
outer tone that clears the effective volume envelope restores
direct cutoff behavior. It is **not** a shared sample setting;
separate direct and Instrument invocations of the same PCM remain
independent. An Instrument tone without a volume envelope retains
the sample's immediate Note Off behavior rather than creating an
unbounded release.

For an **unlooped finite** sample, ordinary PCM exhaustion can still
end audio before a long envelope release; no extra PCM is synthesized.
A looping sample can sustain until the envelope has reached its end.
Explicit Cut/Fade, NNA-Off displacement, and natural sample ends
continue to obey their respective independent termination policies.
No file-format property or sample metadata change is required.

A **zero-duration** volume-envelope release completes at the exact
Note Off frame. `PlaybackSession` now retires that voice on delivery
by consulting its composed source/fade/envelope end, without requiring
an additional PCM `Render` call. The same rule applies when playback
input ends. When NNA Off displaces an Instrument voice whose volume
envelope completes at that exact frame, it skips retaining that
finished envelope tail in the displaced-voice pool. For bare samples
whose *source* ends at Note Off, the existing NNA migration/cleanup
contract is preserved. The `IsQuiescent` state reflects a completed
zero-release tail immediately, including for an explicitly targeted
virtual note.
Longer envelope releases retain their normal continuation and
independent retirement times.

A private Pattern/Sequence tone obeys an additional lifecycle boundary:
the enclosing Instrument's Note Off calls `EndInput` on the private
renderer, which independently sends Note Off to its **child voices**.
Consequently, the outer Instrument's volume envelope cannot make
a *bare child Sample* continue after the child's immediate Sample
Note Off. If the private child itself selects that Sample through
an Instrument with a volume envelope, the child's release may
continue normally while the outer envelope also releases; their
gains multiply and either the finite child PCM end or an envelope
end can terminate the sound. Delayed S77/S78 controls on the parent
pause the **outer** envelope, not every independent child envelope.
Production-path tests cover these distinctions through nested
Instruments, an actual private Sequence, repeat Off, NNA Off, finite
samples, and both render block sizes.

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
