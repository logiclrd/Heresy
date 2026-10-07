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
  SDL implementation. `Heresy.Render.Realtime` owns the backend-neutral
  interleaved-float PCM contracts and `Heresy.Render.SDL` provides the SDL3-CS
  default-playback implementation. Desktop transport wiring is now in place and
  SDL initialization is deferred until first playback.
- [ ] Add realtime decoders for the other sample formats accepted by the authoring UI (FLAC, MP3, OGG and AIFF). Current realtime playback decodes RIFF/WAVE PCM and IEEE-float assets, including synthetic `.hm` archive paths.
- [x] Add tracker transport controls on top of the background playback thread and concrete snapshot-to-PCM source factory:
  - `F5`: play the song root from the beginning.
  - `F6`: play the current tracker pattern repeatedly from row zero. Standalone
    pattern compilation consumes `Bxx`, so a jump ends the current cycle and
    repetition resumes at the pattern start rather than jumping sequence order.
  - `F7`: start on the current row in the closest sequence context that can be
    established. The sequence from which the tracker view was opened takes
    priority even when it is not the song root; otherwise a containing root/data
    sequence is found, with standalone pattern playback as the fallback.
  - `F8`: stop playback.
  The Avalonia app constructs the SDL transport lazily on first playback so
  editing does not require SDL initialization.
- [x] Let the background playback thread accept ordinary sequence playback
  requests (with an optional order/row starting position), repeating/current
  pattern requests, and ad hoc note/row audition requests. The generic worker
  receives immutable request objects and delegates concrete source construction
  through `IBackgroundPlaybackSourceFactory`.
- [x] Deliver a clone/snapshot of the song to the background playback thread
  rather than allowing playback to observe the mutable authoring document.
  `SongDocumentSnapshot` now deep-clones the document before submission,
  preserving stable IDs/assets/tombstones while recording the source revisions.
  This formalizes the separation between authoring state and the object graph
  currently being played.

## Pattern audition

- [x] In the pattern editor, while the cursor is in the Note column:
  - top-row `4` auditions the current note, then advances the cursor one row;
  - top-row `8` auditions the complete current row, then advances one row.
  Both compile immutable ad hoc schedules and deliver them through the existing
  background playback transport. Audition compilation primes sequencing memory
  from earlier rows so omitted Source values and tracker effect-memory recalls
  resolve at the current location. Note audition deliberately uses only the
  current cell's note/source/volume and ignores that cell's effect stack; row
  audition executes the full row including effects.
- [x] While the cursor is in the Note column, physically holding Caps Lock while
  pressing a tracker piano key (`Z S X D C ...`) previews the corresponding
  note without assigning it. Caps Lock acts as a momentary preview modifier; its
  toggled/locking state is irrelevant. Each held tracker key owns an independent
  explicitly targeted virtual channel, repeated key-down events do not retrigger
  it, and releasing that same physical tracker key sends Note Off to that virtual
  channel even if Caps Lock was released first. Window deactivation/editor
  detachment releases every still-held preview voice as a safety net for lost
  key-up events. Preview pitch follows the current tracker octave and source
  resolution is primed from earlier pattern rows just like ordinary note
  sequencing.
- [x] Tracker note entry also feeds the live editing session. Entered notes use
  their real physical tracker channel and are not tied to key-up: they remain
  active until another entered note on that channel displaces the old voice with
  Note Off semantics, an explicit tracker Note Off/Cut is entered, or F8 stops
  the session. Separate tracker channels therefore remain independently
  polyphonic while composing. Held Caps-Lock previews share the same live
  PlaybackSession but use virtual-channel targets, so previews never steal the
  persistent edit voice on a physical tracker channel. The transport lazily
  creates this live session from a fresh immutable document snapshot and reuses
  it for subsequent edit/preview events until another transport request or F8
  replaces/stops it.

## Pattern/object switching and editor state

- [x] Switch between tracker-editable patterns with `+` / `-`.
  - In a sequence-backed tracker view, jump to the first visible row of the
    next/previous editable pattern occurrence in sequence order. Repeated
    occurrences remain distinct; script, missing and zero-row segments are
    skipped. The channel and field are preserved where possible.
  - In a standalone tracker view, follow the rendered depth-first order of
    data-pattern placements in the Patterns tree. Tree placement identity is
    preserved, so duplicate placements remain distinct. Script/missing entries
    are skipped.
  - Navigation stops at either end rather than wrapping. Standalone switches
    preserve the current pattern row, channel, field, Source selection and
    octave, clamping row/channel only when the destination pattern is smaller.
- [x] Switch between Sources (the generalized instrument selection) with `<` /
  `>` and `Ctrl+Up` / `Ctrl+Down`, following the exact order presented by
  the Source drop-down. Navigation clamps at the first/last item rather than
  wrapping; with no current selection, forward chooses the first item and
  backward chooses the last. Ctrl+Shift and Ctrl+Alt arrow combinations remain
  available to higher-level editor commands.
- [x] Change the current octave with physical numpad `*` / `/`. The value is clamped to the tracker octave range `0..8` and the toolbar selector stays synchronized. Ctrl/Alt/Meta-modified keypad operators are deliberately left available to higher-level commands such as chord editing.
- [ ] Add edit masks. The default mask is Note + Instrument + Volume; `,` cycles
  through the available mask choices.
- [x] When focus is on the tracker Source field (the generalized Instrument
  field), Enter selects that explicitly stored source in the surrounding toolbar.
  Omitted Source cells deliberately do not resolve channel source memory for this
  UI command, and missing/deleted source IDs leave the current toolbar selection
  unchanged.

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
  pattern editor. When the user plays or inputs a chord by pressing a note to
  act as the root key, the notes shown in the status bar update to the specific
  notes for the specified chord root.
- [ ] Once a chord is active, pressing a tracker note key transposes the chord
  to that root and inserts its enabled notes into successive pattern channels,
  starting with the current channel. Notes that would extend past the right edge
  are simply dropped.
- [ ] `Ctrl+Alt+-` / `Ctrl+Alt++`: rotate the chord tones, moving the first tone
  to the end or the last tone to the beginning respectively.
- [ ] `Ctrl+Alt+Numpad *`: add chord tones by repeating the chord/scale through
  higher octaves, add one additional note per press. `Ctrl+Alt+Numpad /`: remove
  chord tones, one per press.
- [ ] `Ctrl+Alt+1`, `Ctrl+Alt+2`, `Ctrl+Alt+3`, ... toggle the corresponding
  chord tones enabled/disabled. Disabled tones remain in the status bar but are
  grayed out. Only enabled tones consume destination channels during insertion.
  Enabled/disabled state by index is remembered through chord changes.
  `Ctrl-Alt-=` sets all current notes enabled.

## Pattern row insertion and deletion

- [ ] `Insert` / `Delete`: insert/delete rows in the current channel only.
- [ ] `Alt+Insert` / `Alt+Delete`: insert/delete rows across the full pattern
  width, affecting all channels.

## Pattern navigation

- [x] `Alt+Left` / `Alt+Right`: move to the same field in the adjacent channel
  in one step, clamping at the first/last channel. In sequence-backed views the
  current pattern occurrence's channel count is used. Expanded effect strips keep
  their existing Alt+Left/Right effect-reordering behavior; whole-channel movement
  applies when the tracker field is collapsed.
- [x] `Home` / `End`:
  - normally move to the first/last keyboard field of the current cell;
  - if already at that edge field, move to the first/last channel while staying
    on the current row;
  - the first field is Note. The last field is Effect Parameter, except a single
    native effect occupies one whole effect stop so its last field is Effect
    Command;
  - expanded effect selection collapses before plain Home/End navigation.
    Modified Home/End combinations remain available to their own commands.
- [x] `Ctrl+Home` / `Ctrl+End`: move to the top-left / bottom-right
  of the current tracker-editable pattern occurrence first. In a sequence-backed
  tracker, an occurrence's first visible row respects its sequence `StartRow`;
  repeated occurrences are distinct. Top-left is that occurrence's first visible
  row, channel 1, Note. Bottom-right is its last visible row, last channel, and
  the same last-keyboard-stop rule as plain End (Effect Parameter normally,
  Effect Command for a single native effect). Only when the cursor is already on
  that exact local corner cell does another Ctrl+Home / Ctrl+End escalate to the
  absolute first / last editable cell of the whole editor context. In a
  standalone pattern the local and absolute corners are therefore the same.
  Shift/Alt/Meta-modified forms remain reserved.
- [x] `Ctrl+PageUp` / `Ctrl+PageDown`: move to the first / last
  visible row of the current tracker-editable pattern occurrence while preserving
  the current field and channel. If already on that boundary row, Ctrl+PageUp
  moves to the first visible row of the preceding editable pattern occurrence and
  Ctrl+PageDown moves to the last visible row of the next editable occurrence.
  Sequence navigation skips script, missing and zero-row entries; repeated
  occurrences remain distinct and `StartRow` is respected. If the destination
  pattern is narrower, the channel clamps to its last channel. For standalone
  patterns opened from the tree, the same boundary press switches through the
  same tree order used by `-` / `+`, landing on the first row of the previous
  or last row of the next pattern. Navigation does not wrap. Expanded effect
  selection collapses as part of the move. Shift/Alt/Meta-modified forms remain
  reserved for other commands.
- [x] `Tab` / `Shift+Tab`: jump between Note-column keyboard stops
  on the current row. Tab always seeks the next Note stop to the right, so from
  any field in a channel it moves to the next channel's Note field. Shift+Tab
  seeks the previous Note stop to the left: from Source/Volume/effect fields it
  returns to the current channel's Note field, while from a Note field it moves
  to the preceding channel's Note field. Navigation uses the current underlying
  pattern occurrence's channel count, stops at the outer channel edges rather
  than wrapping rows or patterns, and consumes the key at those edges so focus
  remains in the tracker. Ctrl/Alt/Meta-modified Tab combinations remain
  reserved.

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

## FM Synthesis

- [ ] `ISound` implementation in the same category as samples that generates sound
  using a generic multi-operator FM scheme built on a configurable graph combining
  nodes of type:
    * Constant: Emits a specific (configurable) value constantly.
    * Oscillator: Emits a waveform at a frequency and amplitude. Has an optional
      input for a multiplier and parameters for frequency, Vmin, Vmax and exp, the
      latter of which treats the multiplier as a tone shift and computes the
      actual multiplier as `2 ^ (e / 12)`.
    * Envelope: Emits a value based on the same envelope configuration as is used
      for instrument definitions.
    * Operator: Combines inputs using a simple math operation, such as addition,
      multiplication, min, max.
- [ ] Editor for FM-synthesized instrument specifications that allows the graph to
  be edited and configured using the mouse to drag nodes around. The connections
  between nodes automatically form from orthogonal segments that make a best effort
  to route around nodes but which can be edited by the user. This means that nodes
  need to remember their physical position and connections need to remember hints
  for their routing (which do not functionally affect the behaviour of the graph).
- [ ] Saving and loading of FM-synthesized instrument specifications in `.hm.json`
  documents.
- [ ] Loader that allows an FM-synthesized instrument to be loaded into the Samples
  pane by selecting an existing `.hm` or `.hm.json` file, upon which the
  FM-synthesized instruments in that file are enumerated and the user can select
  one or more to be imported into the current song.
