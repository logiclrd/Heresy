# Heresy TODO

This file records planned authoring, playback and tracker-workflow features that
have been specified closely enough that their intended behavior should be
preserved during implementation.

## Instrument and waveform editing

- [x] Display decoded sample PCM graphically in the Sample editor. The original
  wording called this an instrument waveform, but Heresy's recursive
  `InstrumentDefinition` has no single PCM waveform: its tone specifications
  may resolve to samples, instruments, patterns or sequences. The waveform
  therefore lives with `SampleDefinition`, which owns both the immutable PCM
  and the loop metadata. A framework-independent peak-envelope reducer produces
  per-pixel min/max values without copying PCM, and the Avalonia view draws one
  stacked lane per source channel. Relink/reload refreshes the graph immediately.
- [x] Depict the active sample loop directly on the waveform as a translucent
  range with start/end handles. Handles are interactive only when looping is
  enabled. Dragging previews the candidate boundary in both the waveform and the
  numeric frame fields without mutating the document; releasing the pointer
  commits once through the ordinary sample-metadata editor. Start/end are
  clamped to decoded PCM, cannot cross, and an active loop always retains at
  least one frame. Losing pointer capture cancels the drag and restores the
  original range. Manual numeric edits followed by Apply update the waveform in
  the other direction. Active loop metadata is now rejected at authoring time
  if it lies outside the decoded PCM, matching the renderer's existing
  invariant.
- [x] Add a loop assistant that searches near the current handles for natural
  boundaries that reduce audible loop discontinuities. Forward loops score the
  actual wrap seam across every source channel using both sample-value jump and
  slope mismatch; ping-pong loops instead seek locally flat turning points
  because playback reverses direction at each endpoint rather than wrapping
  end-to-start. The search uses bounded coordinate descent in both start/end
  orders, so its work grows linearly with the chosen search radius instead of
  trying every start/end pair. The Sample editor exposes an adjustable
  millisecond radius (20 ms by default) and **Find natural loop** updates only
  the waveform and numeric fields as a suggestion, reporting the estimated
  discontinuity improvement. The document remains unchanged until **Apply** is
  pressed. Search windows clamp to decoded PCM, all source channels participate,
  non-finite candidate samples are rejected, equal-quality candidates prefer the
  smallest movement from the user's existing loop, and existing out-of-range
  handles are normalized before searching.

## Realtime audio and playback architecture

- [x] Introduce an abstraction for the realtime audio back-end and provide an
  SDL implementation. `Heresy.Render.Realtime` owns the backend-neutral
  interleaved-float PCM contracts and `Heresy.Render.SDL` provides the SDL3-CS
  default-playback implementation. Desktop transport wiring is now in place and
  SDL initialization is deferred until first playback.
- [x] Rework sample storage around the load/import-time PCM model in
  `docs/sample-storage.md`. The live song and playback snapshots own immutable
  decoded PCM; realtime rendering never opens files/ZIP entries or runs an audio
  decoder. Loading a song hydrates its persisted sample assets up front.
  Importing an external audio file copies and decodes it immediately, retaining
  the exact encoded bytes only until the current song successfully saves its own
  copy. Persisted encoded copies are then save-time provenance only and are
  verified by signature before reuse. The old playback-layer WAVE decoder has
  been removed.
- [x] Add load/import-time codecs for FLAC, MP3, OGG Vorbis and AIFF
  alongside WAVE. FLAC and MPEG audio use the managed
  `Hawkynt.FileFormats.Audio` codecs; Ogg Vorbis uses the managed `NVorbis`
  decoder; ordinary AIFF PCM is converted from its big-endian representation by
  the Core load/import codec layer. AIFC `NONE`/`twos`, `sowt`,
  `fl32`/`FL32` and `fl64`/`FL64` are also accepted. Compressed
  representations remain encoded on disk/pending-save as appropriate, but every
  loaded playable sample is immutable float PCM in memory before realtime
  playback can begin. No decoder is invoked by `Heresy.Playback`.
- [x] Let the Samples pane import samples from another `.hm` or
  `.hm.json` song. The ordinary Samples import picker accepts Heresy songs,
  loads/hydrates the source document through the normal persistence layer, and
  presents its sample objects in a multi-selection dialog. Each selected sample
  becomes a new object in the current song with copied metadata, shared
  immutable decoded PCM, and an owned pending copy of the exact encoded
  representation. No Asset/path dependency on the source song or module is
  retained, so the source can disappear immediately after import; the pending
  encoding is released normally after the current song saves its own copy.
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
- [x] While playback is traversing a pattern, highlight the row currently
  being played in realtime in the visible pattern editor. Playback compilation
  now emits an exact pattern-row timeline from the same resolved tracker timing
  used to build the note schedule, so tempo/speed changes, pattern delays,
  pattern loops, start rows and data-sequence order/break flow all move the
  highlight at their actual wall-time boundaries. Sequence playback carries the
  sequence ObjectId and entry index so repeated occurrences of the same pattern
  highlight only the occurrence being played; standalone pattern playback
  highlights every visible occurrence of that underlying pattern row. The
  transport publishes row changes to the UI and clears the position immediately
  when playback is stopped (including `F8`) or reaches its finite logical end;
  repeating pattern playback wraps the row timeline with the audio cycle. The
  row number and all channel cells use
  `UserInterfaceConfiguration.PatternPlaybackRowHighlight`, whose default is
  `Color.FromArgb(0x80, 0x00, 0x80, 0x00)` (50% opacity medium green).


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
- [x] Add traditional tracker edit masks for Note + Source + Volume. All
  three fields are enabled by default. `,` toggles the mask bit for the field
  under the cursor, so every combination is possible. Entering a tracker note
  applies only the enabled fields: Note writes the typed note, Source stamps the
  current toolbar Source, and Volume stamps the current edit-volume value;
  disabled fields preserve the row's existing data. The edit-volume value is
  remembered from completed direct Volume-column entry, with `.` making that
  value empty. Mask/edit-volume state survives standalone pattern switching.
  Effects are not yet part of the edit mask because Heresy has no current
  effect-value entry state to stamp.
- [x] When focus is on the tracker Source field (the generalized Instrument
  field), Enter selects that explicitly stored source in the surrounding toolbar.
  Omitted Source cells deliberately do not resolve channel source memory for this
  UI command, and missing/deleted source IDs leave the current toolbar selection
  unchanged.

## Chord input

- [x] Add chord-entry state to the pattern editor. `Ctrl+Alt` plus the physical
  bottom-row tracker keys chooses a chord type:
  - `Z`: major
  - `X`: minor
  - `C`: dominant seventh
  - `V`: major seventh
  - `B`: minor seventh
  - `N`: half-diminished seventh
  - `M`: diminished seventh
  The state is layout-independent, lives with the pattern editor, and is
  snapshotted/restored when a standalone editor switches patterns.
- [x] Show the current chord immediately above the tracker grid. Before a root
  has been played the bar shows the selected chord type; pressing a physical
  tracker piano key establishes the concrete root and updates the bar to the
  resulting tracker note names. Disabled tones remain visible but are grayed
  out.
- [x] Once a chord is active, pressing a pitched tracker key in the Note field
  transposes the voicing to that root and inserts enabled tones into successive
  pattern channels starting at the current channel. Disabled tones consume no
  channel. Tones extending past the right edge are dropped. The current
  Note/Source/Volume edit mask is applied independently to every enabled
  destination, preserving disabled destination fields, and the whole chord
  edit advances the document/audio revision only once. The cursor advances one
  row after the chord while staying on its original channel.
- [x] Chord entry feeds the existing live editing session once per enabled
  destination channel when the Note edit-mask bit is active, so entered chords
  sound immediately with the same persistent per-channel semantics as ordinary
  note entry.
- [x] `Ctrl+Alt+-` / `Ctrl+Alt++` rotate the voicing by moving the first tone
  to the end or the last tone to the beginning respectively. The moved tone
  crosses an octave as needed so the stored voicing remains strictly ascending;
  for example C-E-G rotates forward to E-G-C5.
- [x] `Ctrl+Alt+Numpad *` adds one tone per press by continuing the chord's
  pitch-class cycle into higher octaves from the current voicing.
  `Ctrl+Alt+Numpad /` removes one tone per press, retaining at least one tone.
- [x] `Ctrl+Alt+1` through `Ctrl+Alt+9` toggle the corresponding current chord
  tones enabled/disabled. Only enabled tones are inserted and consume
  destination channels. Enabled state is remembered by tone index through chord
  changes and through remove/re-add operations. `Ctrl+Alt+=` re-enables every
  current tone.

## Pattern row insertion and deletion

- [x] `Insert` / `Delete`: insert/delete row data in the current
  channel only. Pattern length remains fixed: Insert clears the current row
  position and shifts that channel downward, dropping data shifted past the last
  row; Delete shifts later data upward and clears the final row position. The
  cursor remains on the same row/field/channel. In a sequence-backed tracker the
  displayed row maps to the underlying pattern row before the shift is applied.
- [x] `Alt+Insert` / `Alt+Delete`: perform the same fixed-length row shift
  across the full width of the underlying pattern. The existing effect-stack
  shortcuts retain priority while the cursor is in an effect field:
  Alt+Insert inserts an effect before the selected effect, Alt+Shift+Insert
  inserts after it, and Alt+Delete removes the selected effect. Outside effect
  fields, Alt+Insert/Delete are the full-width row operations. Shift/Ctrl/Meta
  variants not otherwise assigned remain reserved.

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

- [x] Mark rectangular tracker-cell regions by display row and channel; subfields
  inside a cell are not separate selection units. `Alt+B` sets one corner and
  `Alt+E` sets the other, with the visible block normalized regardless of which
  physical corner was set first.
- [x] `Alt+D` marks the current channel for the current pattern's
  major-highlight row count starting at the cursor. Repeating it while the
  cursor remains inside that block expands its height to the next power-of-two
  multiple of the major-highlight size. The block is clamped to the current
  pattern occurrence in a sequence-backed editor.
- [x] `Alt+L` marks the entire current channel across the current pattern
  occurrence. If that channel is already fully covered by the marked region,
  another `Alt+L` marks the whole pattern occurrence across all of its
  channels.
- [x] `Shift+Arrow` uses the ordinary tracker cursor movement rules while
  setting/extending the marked-region end to the new cell. If no region exists,
  the cell where the Shift movement began becomes the first corner.
- [x] `Alt+U` clears the marked region.
- [x] Marked cells use a configurable translucent selection highlight while the
  active cursor and field keep their independent focus borders. Pattern layout
  mutation clears the selection because its row/channel geometry may no longer
  be valid.

## Pattern clipboard

- [x] `Ctrl+C` copies the marked rectangular region as a versioned Heresy
  pattern-region text payload. Available source cells are represented even when
  empty so overwrite paste can reproduce emptiness exactly; cells that do not
  exist because a sequence row's pattern is narrower are omitted rather than
  treated as empty. Pattern notes and effects reuse the same polymorphic JSON
  representation as song persistence/effect-stack clipboard data.
- [x] `Ctrl+V` pastes the copied region starting at the cell under the cursor
  with merge semantics. Empty copied fields are transparent; populated Note,
  Source and Volume fields replace their destination counterparts, and a
  non-empty copied effect stack replaces the destination effect stack as one
  field. Empty copied cells therefore do nothing in merge mode.
- [x] `Shift+Ctrl+V` pastes with overwrite semantics. Every available copied
  cell is reproduced exactly, so empty copied fields clear destination data.
  Both paste modes clip at the bottom of the editor context and independently at
  each destination row's channel width, and may flow across editable pattern
  occurrences in a sequence-backed tracker.
- [x] `Ctrl+X` copies the marked region and clears it only after the system
  clipboard write succeeds. `Ctrl+Delete` clears the marked region without
  copying. Multi-cell clear/paste commands produce one document revision and
  only advance the audio revision when the changed data can affect playback.
- [x] Existing effect-stack `Ctrl+C` / `Ctrl+V` remains available in an
  effect field when no region is marked/copying and the clipboard does not
  contain a pattern-region payload. Region copy takes priority whenever a block
  is marked.

## FM Synthesis

- [x] Implement the executable FM graph substrate as a validated immutable DAG in
  Core plus `FmSynthSound` in Render. The graph has stable integer node IDs and
  one designated output node; missing inputs, duplicate IDs, missing output nodes
  and cycles are rejected before rendering. Current semantic nodes are:
    * Constant: emits one finite configurable scalar.
    * Oscillator: sine, triangle, sawtooth or square output mapped through
      configurable `Vmin`/`Vmax`, with positive base frequency and an optional
      multiplier input. Linear mode multiplies frequency directly; exponential
      mode interprets the input as semitones and applies `2 ^ (e / 12)`.
      Oscillator frequency also composes the note pitch, playback-speed multiplier
      and current pitch trajectory.
    * Envelope: references the same persistent envelope IDs used by instruments
      and evaluates them through `IEnvelopeCurveResolver`; unresolved references
      produce zero so broken graph references remain deterministic.
    * Operator: combines one or more inputs using addition, multiplication,
      minimum or maximum.
  `FmSynthSound` renders the scalar graph as an ordinary mono positional Heresy
  source, using the common spatializer for the configured output layout. Mutable
  oscillator phase lives only in per-note state. Sequential/split renders are
  identical, and non-contiguous rendering deterministically replays phase from
  frame zero rather than depending on prior chunking. Graph collection surfaces
  are frozen snapshots so a future editor cannot mutate an already-bound graph
  accidentally.
- [x] Make FM synthesis a persistent Samples-pane song object. `FmSynthDefinition`
  owns the immutable semantic graph plus separate editor-only node-position and
  orthogonal-routing-hint metadata. `SongObjectKind.FmSynth` belongs to the
  Samples tree section. JSON persistence stores the graph and editor layout
  separately, `SongDocumentSnapshot` deep-clones both through persistence, FM
  envelope-node ObjectIds participate in reference/tombstone analysis, and FM
  tombstones round-trip with their own kind.
- [x] Treat persistent FM synths as ordinary tracker sound Sources. They are
  included in the Source catalog alongside sample-like objects, and
  `PlaybackRequestAudioSourceFactory` resolves an FM object from the immutable
  playback snapshot to `FmSynthSound`, reusing the same snapshot envelope
  resolver as instrument playback for FM envelope nodes.
- [x] Editor for FM-synthesized instrument specifications. The Samples pane can
  create FM synth objects directly and opens them in a main-workspace graph editor.
  Constant, oscillator, envelope and operator nodes can be added, selected,
  parameterized, connected, made the output and removed subject to graph validity.
  Nodes are dragged with the mouse; the drag is preview-only until release, then
  the persisted node position advances DocumentRevision without touching
  AudioRevision. Semantic graph edits replace the immutable graph and advance
  AudioRevision. Connections render as orthogonal segments using deterministic
  dogleg routing first and above/below obstacle lanes when necessary. Per-connection
  persisted routing hints are editable as explicit waypoint lists in the inspector;
  hinted routes remain orthogonal and preserve the user's waypoints even when a
  waypoint is geometrically redundant. Routing hints and node positions remain
  editor-only metadata and never participate in synthesis.
- [x] Saving and loading of FM-synthesized instrument specifications in `.hm.json`
  documents. Consolidated `.hm` packages inherit the same representation through
  their JSON manifest.
- [x] Import FM-synthesized instruments into the Samples pane from an existing
  `.hm` or `.hm.json` song. The source song's FM synth objects are enumerated
  in object-ID order and presented in a multi-selection dialog. Each selected
  synth receives a fresh object ID while retaining its immutable graph topology,
  stable graph-node IDs, output selection, node positions and routing hints.
  Envelope nodes never retain source-song ObjectIds: every referenced source
  envelope is imported automatically with a fresh ID and the graph reference is
  remapped. Envelopes shared by multiple selected synths are imported only once
  and remain shared; unrelated envelopes are not copied. A missing or unsupported
  referenced envelope rejects the batch before the target document is modified.
