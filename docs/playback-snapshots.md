# Playback snapshot freshness and authoring feedback

Heresy's realtime playback consumes an **immutable `SongDocumentSnapshot`**
captured when each Sequence, Pattern, song or ad-hoc request is created.
A completed request starts a new session on the dedicated PCM worker;
subsequent authoring changes cannot modify the already-running snapshot.

## Separate document and audio revisions

`SongDocument.MarkChanged(affectsAudio)` increments `DocumentRevision`
for every change, but increments `AudioRevision` **only** for
audio-affecting changes. `DocumentWorkspace.IsModified` compares the
document revision against the last saved revision. The playback freshness
indicator instead compares the frozen snapshot's audio revision with
the **same authoring document instance**. Saving a modified file does
not refresh an old playback session; reorganizing the tree or other
layout-only authoring changes do not make current playback stale.

`SongDocument.Changed` is published after revision increments, on
the caller's thread, with an `AffectsAudio` classification. MainWindow
subscribes to its current workspace document, moves that subscription
when New/Open successfully replaces the document, and detaches on close.
Asynchronous edit notifications from a replaced document cannot update
the new document's display.

## Transport/session identity

`IPlaybackSnapshotTransport` exposes
`CurrentPlaybackSnapshot` and `PlaybackSnapshotChanged`; the snapshot
contains a monotonically increasing transport generation, the **original
authoring `SongDocument` reference**, and the
`PlaybackRequest.Snapshot.AudioRevision` actually frozen at request
creation. `SongPlaybackTransport` publishes this snapshot only after
a successful background session start, clears it on Stop/failure,
and replaces it on a subsequent successful Play. Events are isolated
from faulty subscribers and are never invoked inside audio callbacks.
`LazySongPlaybackTransport` forwards subscriptions without opening SDL
merely to read the initial inactive snapshot.

For live note audition, Note Off and other existing-note commands
continue using the existing frozen session even after an audio edit.
A subsequent new note detects the revision mismatch and starts a fresh
snapshot, updating its published identity. A different authoring
document always starts a fresh audition session even when its revision
number happens to match.

## User-facing status

The bottom status bar presents a quiet indicator **separate from the
unsaved-file asterisk**:

- Hidden when no session is active, or the current document's audio
  revision matches the playback snapshot.
- **Playback uses older audio** after an audio-affecting authoring edit.
  Restart playback to hear the change.
- **Playback uses another song** when New/Open replaces the active
  authoring document while audio from the old document is still playing.

Snapshot events are posted to Avalonia's dispatcher, with monotonically
increasing generations to reject stale out-of-order posts; document
change events also dispatch when necessary. The indicator does not
mutate the running snapshot and neither changes the PCM worker nor
assumes the song file was saved.

Regression coverage includes layout-only versus audio edits, a
successful second Play reset, stopping, failed replacement, live
Note Off versus new-note refresh, distinct document identity and
lazy transport subscriptions.
