# Heresy TODO

This file records planned authoring, playback and tracker-workflow features that
have been specified closely enough that their intended behavior should be
preserved during implementation.

## Instrument and waveform editing

- [ ] When editing an instrument, display its waveform graphically.
- [ ] Add a graphical depiction of the loop range and allow the loop boundaries
  to be edited directly in that view.
- [ ] Add a loop assistant that searches for a natural loop boundary that avoids
  audible clicking.

## Realtime audio and playback architecture

- [x] Introduce an abstraction for the realtime audio back-end and provide an
  SDL implementation. `Heresy.Render.Realtime` now owns the backend-neutral
  interleaved-float PCM contracts and `Heresy.Render.SDL` provides the SDL3-CS
  default-playback implementation. Desktop transport wiring/native runtime
  selection remains part of the background-playback work below.
- [ ] Add a background playback thread with tracker transport controls:
  - `F5`: play the song.
  - `F6`: play the current pattern repeatedly. In this mode, `Bxx` jumps back
    to the beginning of the current pattern instead of performing its ordinary
    sequence-order jump.
  - `F7`: enter full-song playback mode starting on the current row. Technically,
    start playback in the deepest ancestor sequence that can be found for the
    current view. If that sequence/pattern is not actually anchored to the song
    root, play from the deepest enclosing sequence that can be found.
  - `F8`: stop playback.
- [ ] Let the background playback thread accept both ordinary sequence playback
  requests (with an optional starting offset) and ad hoc note/row audition
  requests.
- [ ] Deliver a clone/snapshot of the song to the background playback thread
  rather than allowing playback to observe the mutable authoring document.
  This formalizes the previously planned separation between authoring state and
  the object graph currently being played.

## Pattern audition

- [ ] In the pattern editor, while the cursor is in the Note column:
  - `4` plays the current note, then advances the cursor one row.
  - `8` plays the current row, then advances the cursor one row.
  Both are ad hoc playback instructions delivered to the background thread.
- [ ] While the cursor is in the Note column, holding Caps Lock while pressing a
  tracker piano key (`Z S X D C ...`) previews the note without assigning it.
  The note keeps playing until that physical key is released, which sends Note
  Off.

## Pattern/object switching and editor state

- [ ] Switch between patterns with `+` / `-`.
  - If a sequence is the parent of the current view, follow that sequence's
    pattern order.
  - Otherwise follow the same ordering as the tree used to open the editor.
- [ ] Switch between instruments with `<` / `>` and `Ctrl+Up` / `Ctrl+Down`,
  following the order in which instruments are presented in the drop-down.
- [ ] Change the current octave with numpad `*` / `/`.
- [ ] Add edit masks. The default mask is Note + Instrument + Volume; `,` cycles
  through the available mask choices.
- [ ] When focus is on the Instrument field for a note, Enter selects that
  instrument in the surrounding UI.

## Chord input

- [ ] Add chord-entry state to the pattern editor. `Ctrl+Alt` plus the physical
  bottom-row tracker keys chooses a chord type:
  - `Z`: major
  - `X`: minor
  - `C`: dominant seventh
  - `V`: major seventh
  - `B`: minor seventh
  - `N`: half-diminished seventh
  - `M`: diminished seventh
- [ ] Show the notes making up the current chord in a status bar above the
  pattern editor.
- [ ] Once a chord is active, pressing a tracker note key transposes the chord
  to that root and inserts its enabled notes into successive pattern channels,
  starting with the current channel. Notes that would extend past the right edge
  are simply dropped.
- [ ] `Ctrl+Alt+-` / `Ctrl+Alt++`: rotate the chord tones, moving the first tone
  to the end or the last tone to the beginning respectively.
- [ ] `Ctrl+Alt+Numpad *`: add chord tones by repeating the chord/scale through
  higher octaves. `Ctrl+Alt+Numpad /`: remove chord tones.
- [ ] `Ctrl+Alt+1`, `Ctrl+Alt+2`, `Ctrl+Alt+3`, ... toggle the corresponding
  chord tones enabled/disabled. Disabled tones remain in the status bar but are
  grayed out. Only enabled tones consume destination channels during insertion.

## Pattern row insertion and deletion

- [ ] `Insert` / `Delete`: insert/delete rows in the current channel only.
- [ ] `Alt+Insert` / `Alt+Delete`: insert/delete rows across the full pattern
  width, affecting all channels.

## Pattern navigation

- [ ] `Alt+Left` / `Alt+Right`: move to the same field in the adjacent channel
  in one step.
- [ ] `Home` / `End`:
  - normally move to the first/last field of the current cell;
  - if already at that edge field, move to the first/last channel while staying
    on the current row.
- [ ] `Ctrl+Home` / `Ctrl+End`: move to the top-left / bottom-right of the
  pattern.
- [ ] `Ctrl+PageUp` / `Ctrl+PageDown`: move to the first / last row while
  staying in the current channel.
- [ ] `Tab` / `Shift+Tab`: jump to the next / previous Note column, crossing to
  the adjacent channel as needed.

## Pattern selection

- [ ] `Alt+B`: set the start of the marked region.
- [ ] `Alt+E`: set the end of the marked region.
- [ ] `Alt+D`: select the major-highlight number of rows starting at the current
  cursor position. If the cursor is already inside a selected block, expand the
  block to the next power-of-two multiple of the major-highlight row count.
- [ ] `Alt+L`: mark the entire current column. If that column is already fully
  marked, mark the entire pattern.
- [ ] `Shift+Arrow`: move the cursor normally while setting/extending the marked
  region end to the new cursor position.
- [ ] `Alt+U`: cancel the selection/unmark the current region.

## Pattern clipboard

- [ ] `Ctrl+C` (`^C`): copy the marked region.
- [ ] `Ctrl+V` (`^V`): paste the copied region starting at the cell under the
  cursor, merging copied data into existing destination data.
- [ ] `Shift+Ctrl+V`: paste with overwrite semantics instead of merge.
- [ ] `Ctrl+X` (`^X`): copy the marked region and then clear it.
- [ ] `Ctrl+Delete`: clear the selected region without copying it.
