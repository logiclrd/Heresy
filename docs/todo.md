# Heresy TODO

This file contains only explicitly specified work that remains open.

## Dialog behavior and layout

- [ ] In every dialog under `Heresy.UserInterface/Dialogs`, mark the dialog's
  accept/confirm button as `IsDefault` and its cancel button as `IsCancel`
  so Enter and Escape perform the expected standard dialog actions.
- [ ] In dialogs that end with a row/array of action buttons, anchor that action
  row to the lower-right of the dialog rather than allowing it to float with the
  preceding content. Audit at least `ConfirmDialog`,
  `FmSynthImportSelectionDialog`, `NativePatternEffectEditorDialog`,
  `SampleImportSelectionDialog`, `TextPromptDialog`, and
  `UnsavedChangesDialog`; preserve the lower-right placement even for
  resizable dialogs.

## FM synth graph editor

- [ ] Draw a small arrowhead at the sink end of every graph connection, pointing
  into the node/input consuming the value. Do not decorate the source end.
- [ ] When the pointer hovers over a node border, show small connection handles.
  Dragging from one handle to a compatible handle on another node should create
  the corresponding graph connection directly.
- [ ] Allow `Remove Node` even when the node is currently consumed by other
  nodes. Removing the node must implicitly remove every connection that uses it
  as an input, while preserving normal graph validity for the remaining graph.
- [ ] Add an FM-synth audition area containing the text `Test`. While keyboard
  focus is in that area, tracker piano keys (`Z S X D C ...`) should audition
  the corresponding notes, and `*` / `/` should change the audition octave
  exactly as they do in the pattern editor.
- [ ] Make node-parameter editing commit immediately when an input loses focus or
  when Enter is pressed. Escape in an input should restore its last committed
  value. Remove the Apply button; there should be no separate apply step.
- [ ] Make connection routing editable directly on the graph. Clicking anywhere
  along a connection and dragging should create a waypoint at that location,
  inserted into the existing waypoint sequence according to the segment that was
  clicked. Connection hit-testing should use a 3-pixel tolerance on either side
  of the rendered line. Double-clicking an existing waypoint should delete it.
  Once this interaction exists, remove the raw waypoint-data editing UI.

## File menu and accelerators

- [ ] Add an Exit command to the File menu. It must use the same normal
  window-close path, including the existing dirty-document Yes/No/Cancel prompt.
- [ ] Add application accelerators: `Ctrl+N` for File -> New, `Ctrl+O` for
  File -> Open, `Ctrl+S` for File -> Save, and `Ctrl+Q` for File -> Exit.
  If any accelerator conflicts with pattern-editor functionality, the pattern
  editor binding takes precedence while focus is in the pattern editor.

## Offline rendering and export

- [ ] Define finite export behavior for songs whose `Bxx` effects create loops.
  During offline song rendering, the third time playback reaches the same
  `Bxx` instruction, treat that occurrence as the end of the song instead of
  following the jump again indefinitely.

Completed implementation history is preserved in Git, while stable architectural
and behavioral rules belong in the focused documentation and README rather than
remaining as checked-off TODO entries.
