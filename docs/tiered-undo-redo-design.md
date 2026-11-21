# Tiered Undo/Redo — approved design (future feature, not PR #38)

**Status:** Contract approved by owner on 19 November 2026. Design only. **No new user-visible history functionality is to be implemented under Duplicate Object PR #38**; its former D6 one-off history work is superseded. The existing guarded Core batch receipt remains a useful transaction primitive and is not a general song-history system.

## Two levels

1. **Whole-song history** is active only in the six-pane main song view. Ctrl+Z and Ctrl+Y move backward/forward across coarse **song-level authored states**. Checkpoint on **completion of an editing session**, including direct editor-to-editor transitions (not literal operating-system keyboard focus changes). Checkpoint any successful discrete operation performed directly in the song view, such as create/delete/rename/import/duplicate or tree reorder. No-op state repetitions are suppressed. Returning from a picker, moving focus among song-tree controls, or switching applications creates no checkpoint. An editor's entire session becomes one song-level step if its resulting song state differs from the last one.
2. **Editor-local histories** live only while their editor session is open. Ctrl+Z/Ctrl+Y are never passed through to song-level history even when no local step is available. Each editor begins a new local history when reopened; navigating away discards that local history after completing/committing outstanding edits. The nested Instrument Filter-parameter dialog owns a third, nested history, independent of its parent editor.

A new authored change after Undo invalidates that tier's Redo branch. Undo and Redo playback do not create new checkpoints. A failed New/Open retains history, successful New/Open start a fresh whole-song history. Save preserves history and its current cursor.

## Per-editor capture boundaries

- **Synth:** one breadcrumb for every completed graph change or authored parameter change. A drag/slider held across many live audio publications is one gesture, closed at release. Undo should focus the affected node/control and may switch the selected node. Snapshots preserve complete state (including disappearing Filter fields, disconnected nodes, graph layout, routing, and linked property groups).
- **Sample:** each committed change is one step.
- **Filter:** add/remove/reorder stage and each committed stage-editor update are steps. Kind-switch snapshots preserve disappearing fields and historical overrides as one transition.
- **Envelope:** each finished node drag, Set nodes click, and committed remaining control edit creates one step.
- **Instrument:** each committed edit is one step; a multi-selected tone edit is one **single batch**. The nested Filter-parameter dialog has its own local history; accept/close makes exactly one parent Instrument step if changed, and cancel makes none. Apply lookup is one step; snapshots retain all discarded configuration when Divisions changes, in both directions.
- **Sequence:** Set as song root, Apply pattern, and per-row Apply/Remove/reorder actions are steps.
- **Object names:** a committed rename is one step for every relevant editor (including editors without a name field today; when implemented they follow this rule).

### Pattern editing: inactivity coalescing and forced flush

- Note/pattern authored edits are combined into a **pending batch**, closed five seconds **after the last grid edit**, never every five seconds during continuous activity.
- Clicking **Apply layout** is a flush boundary and separately records the layout change when one occurs.
- Changing the current channel flushes the previous channel's pending changes before changing channels.
- **Any interaction outside the Pattern grid that interrupts note entry** (including changing Octave or Skip, using an editor control, switching focus out of the grid, or navigating to an outside control) ends the current grid batch immediately. A subsequent grid edit starts a new batch even if the five-second inactivity deadline has not yet passed. Thus two notes, a click to change Octave, and two more notes produce two Undo units; Octave retains its new value after both Undos.
- Skip and Octave are **editor state**, not authored history changes. They do not independently create breadcrumbs and are **not reverted by Pattern Undo**, even if persisted for the next editor session.
- Ctrl+Z during a still-open pending batch **immediately undoes that batch**; it never waits for the timer or requires a first keypress solely to flush. Similarly, Ctrl+Y behaves normally once a batch has been undone.
- Leaving the editor, saving, or another specified commit boundary flushes pending authored changes. No-op flushes do not create duplicate points.

### Script source: separate text-editor and song state

- Source typing produces **editor-local text-buffer history**, coalesced into one breadcrumb five seconds after the most recent change. A subsequent change resets the timer.
- The five-second timeout **does not apply script source to the song**. Several local Undo points may exist even though song source has not changed at all.
- **Apply source** commits the current buffer to the document, first finalizing any open text edit batch, and never inserts an identical redundant local checkpoint. The local snapshot includes enough information to restore editor buffer, authored applied source, caret and selection coherently.
- Explicit actions outside source text, editor leaving, applying source, Ctrl+Z/Ctrl+Y and other interaction boundaries flush a pending typing batch consistently.
- This applies to all script-source fields across scripted object types and any other relevant editor parameters. Caret/selection changes alone are not authored source changes.

## Restoration and invariants

- Undo points store **coherent before/after domain state**, not individual incidental property-change event streams. Filter type changes and Instrument Divisions restore all lost/hidden data. Undo of a node creation may select a sensible surviving node if the original edit target no longer exists.
- Undo/Redo restores the most relevant field, row, channel or node selection where possible; selection/caret is metadata attached to the checkpoint, not necessarily an authored song change.
- Intermediate Synth live updates still publish realtime audio but do not create independent breadcrumbs. Undo/Redo of audio-affecting state must trigger the normal renderer/preview publication and shared-object notifications.
- Document dirty state is **saved authoring state identity/fingerprint equality**, not monotonic revision number equality. Undo to exact saved content clears the modified indicator; Redo makes it dirty again when appropriate. Save does not erase history.
- Whole-song snapshots include graph references, deleted objects, tree organization, scripts, Sample asset ownership and all authored metadata. Share immutable decoded PCM as appropriate; do not repeatedly duplicate large buffers. Provisional or aborted transactions have no history entry.
- Shared objects edited through multiple simultaneously open views require revision-aware invalidation or reconciliation so a local Undo cannot silently destroy a newer change in another view.
- Existing one-level `SongImportUndoHistory` will be **replaced** by this general whole-song history and should not be expanded into another separate subsystem. Imported objects and Duplicate operations become ordinary whole-song breadcrumbs once the new system is implemented.

## Implementation approach (later independent work)

Build shared song-history checkpoint/cursor and saved-state baseline; shared editor-session history with idle batching, gesture/forced-flush boundaries and caret/selection metadata; and domain-specific state capture/restore adapters for Synth, Sample, Filter, Envelope, Instrument, Sequence, Pattern and Script. Integrate keyboard routing by active view, nested dialogs and saved-state comparison. Keep this future project independently bounded and regression-tested rather than sneaking partial history into a tree-context-menu feature.
