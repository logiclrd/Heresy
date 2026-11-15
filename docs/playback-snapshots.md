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
continue using the existing session after an audio edit. Direct Synth
edits may be compiled and published through a revision-aware overlay,
allowing compatible held voices to migrate and new direct Synth notes
to use revised definitions **without replacing the session's base song
snapshot**. Other sounds can still belong to the original snapshot;
a non-Synth note start that cannot use the overlay takes the conservative
fresh-session path. A different authoring document always starts a fresh
audition session even when its revision number happens to match.

## User-facing status

The bottom status bar presents a quiet indicator **separate from the
unsaved-file asterisk**:

- Hidden when no session is active, or the current document's audio
  revision matches the playback snapshot.
- **Playback snapshot is older** after an audio-affecting authoring edit.
  This describes the immutable *base session snapshot*, not proof that
  every sounding note uses an old definition: compatible live Synth
  edits can already have reached the currently held voices. Since other
  voices or Sound definitions may still reflect the original snapshot,
  the indicator must not be hidden or the base revision advanced just
  because some Synths were published. Restart playback to refresh the
  entire session. The tooltip explains this distinction.
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
