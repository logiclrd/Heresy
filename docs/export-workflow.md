# Offline export progress and cancellation

The production offline export pipeline uses the same incremental recursive
coroutine sequencer and PCM engine as realtime playback. It does **not**
materialize the future Sequence or infer an ending for arbitrary scripts.

## Render-progress contract

`OfflineRenderProgress` reports `LogicalFramesRendered`,
`TailFramesRendered`, `SampleRate` and a phase:

- `LogicalBody`: cumulative logical frames that have actually been written
  to the selected sink.
- `ReleaseTail`: logical time remains frozen at the discovered arrangement
  end; tail frames describe additional rendered/encoded release audio.
- `Completed`: the offline PCM body and all finite tails were written.

The value `RenderedMusicalTime` is the logical frame count divided by the
sample rate, **not** elapsed encoding time or wall-clock ETA. The
`KnownLogicalFrameCount` is non-null only for the explicit finite-duration
`PlaybackSession` API. In the production
`IIncrementalArrangementSource` overload it remains null, since a Sequence
can be scripted, repeated or unbounded until consumed. A future UI may
display determinate percentages for a proven finite program, but may not
force a greedy expansion merely to obtain a denominator.

Reporting happens only after a complete block has been written.
`IProgress<OfflineRenderProgress>` lets callers marshal notifications;
the Avalonia dialog coalesces repeated worker reports into **at most one
outstanding UI dispatcher post** while retaining the latest musical-time
position. This avoids an unbounded UI-event backlog if offline PCM renders
much faster than realtime; the worker never touches controls.

## Cooperative cancellation

The renderer checks `CancellationToken` at the beginning of each output
block, between body and tail, and before successful completion. Work already
underway inside one PCM block is allowed to finish; cancellation is not
injected into scripts, note generators, sample renderers or the audio callback.
A user can click Cancel (or close the progress window) while audio is
rendering. The cancel control disables itself while awaiting the worker;
the main window prevents concurrent audio exports.

`SongExportService` captures an immutable song/output-format snapshot before
queuing its background task and **always owns/disposes the plan** once queued,
including when a token is canceled before that task begins. It writes into a
unique temporary file in the destination directory. If rendering, codec
finalization or cancellation fails, the temporary file is deleted without
moving it onto the final path. The token is checked again **after**
`IAudioFileSink.Complete` and **before** the atomic
`File.Move(overwrite: true)` commit point. After a successful commit,
cancellation is no longer accepted: otherwise the program might report an
"aborted" export that already replaced an existing file.

Both the GUI and public API retain the previous behavior with no progress
callback or cancellation token supplied. The renderer's existing finite
body and tail caps remain in force for looping/infinite scripts. The FLAC/MP3 sinks retain their existing PCM render path, while the WAV
sink now supports selectable integer precision and explicit surround
speaker-channel metadata. The buffered realtime architecture is unchanged.

## Realtime/offline PCM parity regression (implemented)

`PlaybackExportPcmParityTests` exercise the **production** realtime
`PlaybackRequestAudioSourceFactory` and offline
`OfflineSongRenderPlanFactory` + `OfflinePlaybackRenderer`, with the
same song objects, sample-data provider, and immutable
`RenderConfiguration`. The test copies float PCM at the
`IAudioFileSink` boundary **before WAV/FLAC/MP3 quantization**, and
compares it sample-for-sample against realtime's worker-source
`IAudioOutputSource.Render` output. Realtime and offline render in
different PCM block sizes (including single-frame blocks), checking
chunk invariance as well as parity. The test mutates the original
document after constructing both sources; neither already-frozen
source may follow those edits.

The eight cases cover mono, stereo, 5.1 and 7.1 layouts with
independent per-speaker LowPass / HighPass filters, direct sample
notes, flattened nested Patterns, simultaneous private mixdowns,
instrument-selected recursive sounds, and scripted child notes with
a Tempo change. Every output channel is compared for the entire
naturally completed logical arrangement, and a non-silent assertion
prevents empty tests from falsely proving parity. Both paths
continue to rely on the same recursive coroutine and deterministic
sample-frame mapping: no eager schedule/journal or new audio path
was introduced.

The comparison deliberately stops at the **logical-body end**:
offline export invokes `EndInput`, cuts indefinite voices as
specified, and renders envelope/filter tails into the file, whereas
realtime playback does not use that export-only termination policy.
Encoding quantization and physical SDL callback/ring-buffer timing
are separately tested contracts, and the latter is not compared
with an actual audio device by this headless suite.

Advanced compatibility testing of unbounded **scripted Patterns**,
mixed tracker flow/time changes and export body/tail caps remains
tracked separately in [todo.md](todo.md).

## Export encoding and format-specific constraints

The **File → Render Audio** save picker offers FLAC (default), MP3,
and WAV integer PCM at **8, 16, 24 or 32 bits per sample**. WAV stays
16-bit by default; picking another depth affects that export only.
All formats receive exactly the same float PCM from the incremental
rendering engine. WAV clips/quantizes PCM to the selected integer width:
8-bit samples are unsigned, 16/24/32-bit samples signed little-endian.
Nonfinite PCM is rejected before a block is written; integer conversion
is symmetric to the negative full-scale endpoint and saturates positive
full-scale to the highest representable signed code.

Mono/stereo WAV retains the canonical 44-byte RIFF/PCM header. 5.1/7.1
WAV uses `WAVEFORMATEXTENSIBLE` with standard ordered speaker masks:
front L/R, center, LFE, rear L/R and (for 7.1) side L/R. The RIFF
container uses even-byte padding after odd-sized data, excludes this
pad from `data` size, and checks the full RIFF size against 32-bit
limits rather than overflowing at final header rewrite.

Before creating any export temporary file, `SongExportService`
validates the actual selected output sample rate and channel count
against encoder constraints. MP3 supports mono/stereo at MPEG rates
8, 11.025, 12, 16, 22.05, 24, 32, 44.1 or 48 kHz; it explicitly
rejects unsupported multichannel or high-rate output rather than
delegating an opaque error to libsndfile. FLAC supports 1–8 channels
and rates up to 655350 Hz; the application dialog's own range is
8–384 kHz. WAV supports the application's mono/stereo/5.1/7.1
layouts and the four named integer precisions. Unsupported combinations
fail without replacing the existing destination or leaving temporary
files. These checks do not resample or silently change speaker layouts.

`WavePcmBitDepthTests` and `SongExportServiceTests` cover exact
quantized sample bytes, header fields, surround channel masks, odd
RIFF alignment, invalid precision and format combinations, and
transactional service-level WAV precision selection. A physical
multichannel codec/device compatibility survey is a separate task.

## Tests

Regressions cover indeterminate progress and cancellation in an indefinite
incremental arrangement, explicit finite-duration release-tail events,
pre-canceled exports, and cancellation after a written body block that must
preserve the destination and leave no `.heresy-render.tmp` file.

## Bounded runtime sequencing diagnostics

Realtime and offline rendering share the `SequencingDiagnostic` types
and suppression policy: HRSEQ001 denotes a dropped out-of-order
note, HRSEQ002 suppresses further such warnings, HRSEQ003 reports
ignored voice-specific effects on a flattened source, and HRSEQ004
suppresses further such warnings. `SequencingDiagnosticLog` permits
up to 32 individual warnings in each category plus its corresponding
suppression notice. Draining a log does not reset these limits.

`OfflineSongRenderPlan.Diagnostics` exposes the plan's bounded log.
Flattened child contexts already share this log; recursively created
**private Pattern/Sequence mixdown contexts** now receive the same
log explicitly, including private tones selected indirectly by
Instruments. This keeps nested warnings visible in both export and
realtime playback without changing their audio or ownership.

`SongExportService.ExportAsync` accepts an optional
`IProgress<SequencingDiagnostic[]>` argument. Its block progress
adapter drains the log after each completed PCM block **before**
notifying the ordinary musical-time progress observer. A `finally`
drain forwards messages produced after the last progress event if
encoding, rendering or cancellation aborts the operation. Empty
batches are never reported. No diagnostic callback is invoked by
the sequencer itself. A canceled export keeps the destination
intact but **does not suppress warnings from rendered music**.

The Avalonia `MainWindow` passes a `Progress<SequencingDiagnostic[]>`
reporter to the export service. UI-context delivery joins export
messages, prefixed `[Export]`, into the existing Warnings button
and Runtime Diagnostics window. The existing clear action and
last-500-history-entry bound apply equally to export and realtime
reports. The UI never handles callbacks on the PCM worker; a closed
window discards pending UI notifications safely.

Regression tests cover warning delivery from a 48-row song
(including the 32-message cap and single suppression notice),
no duplicate reports across blocks/final cleanup, preservation
of pending warnings on cancellation and failure, and warnings
originating inside a private nested mixdown. The existing atomic
export destination and temporary-file cleanup behavior remains
unchanged.
