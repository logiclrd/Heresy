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
body and tail caps remain in force for looping/infinite scripts. Existing
FLAC/MP3/WAV sink implementations and the buffered realtime architecture
are unchanged.

## Tests

Regressions cover indeterminate progress and cancellation in an indefinite
incremental arrangement, explicit finite-duration release-tail events,
pre-canceled exports, and cancellation after a written body block that must
preserve the destination and leave no `.heresy-render.tmp` file.
