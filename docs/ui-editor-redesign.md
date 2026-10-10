# Planned UI polish and instrument/editor redesign (October 10, 2026)

Status: **design/TODO capture only**. The behavior below is requested and not
implemented by this document. Preserve these acceptance rules when the
individual UI milestones are built. The current Core `InstrumentDefinition`
still has a separately stored `ToneSpecifications` list and `ToneTable`
indices, `AdsrEnvelopeDefinition` currently stores TimeSpan Attack/Decay/
Release and an unrestricted finite SustainLevel, and the document tree
still has four fixed `SongTreeSection` roots. UI work must reconcile those
actual models explicitly rather than assuming a redesign is already complete.

## Pattern editor: optional playback-follow

- Add a **Follow** checkbox in the pattern editor. Its value controls a
  playback-follow flag, not a change to playback/transport state.
- All three keyboard inputs toggle that same flag: bare backtick (`\``,
  Renoise), **Ctrl+F** (OpenMPT), and **Scroll Lock** (MilkyTracker).
  Preserve the priority of the pattern editor's existing key bindings and
  avoid toggling twice for one key event. The checkbox and keyboard state
  must remain synchronized. **Known keyboard conflict:** the current
  tracker Note column uses backtick to enter Note Off (documented in
  README). The new Follow binding is specifically requested; its
  interaction with existing Note Off entry must be resolved explicitly
  by focus/shortcut priority instead of silently dropping either
  behavior or claiming there is no conflict.
- While enabled, each new playback-highlighted row should scroll into view
  **as near the vertical middle of the visible pattern viewport as the
  available scroll extent allows**. At the start/end, clamp naturally to
  the document edges. When disabled, playback may continue highlighting
  rows but must not change the user's scroll position.
- Handle the existing `PatternEditorContext` display rows, including a
  Pattern viewed inside a Sequence, sequence segment headers, and recurring
  references. Follow the row resolved by current playback position without
  moving the edit cursor or stealing keyboard focus. Keep programmatic
  viewport tracking separate from manual selection/edit navigation.

## Document panes and vocabulary

- Promote **Envelopes** to a first-class document-tree section and its own
  pane. The document view must have **two upper panes** (Sequences,
  Patterns) and **three lower panes** (Samples/Patches, Envelopes,
  Instruments). Update persistence/tree-root mappings, add/create
  placement, drag/reorder constraints, existing placements/migration policy,
  selection and editor routing together; object IDs/references must survive.
- Where UI labels **Sample/Samples** currently denote sources which can
  also be FM synthesizers, change those labels to **Patch/Patches**.
  Retain *Sample* where the operation or data is specifically about
  sampled PCM (e.g. sample import, loop, waveform and sample-format
  controls). Preserve the underlying Core type names and file formats
  unless a separate model migration is explicitly warranted.

## Graphical ADSR envelope editor

- Add a reusable graph-based envelope editor, backed by the **same live
  `AdsrEnvelopeDefinition`** as the current numeric inspector/editor.
  Render the shape of Attack, Decay, Sustain and Release.
- At the **right edge** of the graph, show labels for Y=0 at the bottom,
  **Note Volume** at the top and a **Sustain** marker tracking the sustain
  line's actual level. These are the requested conceptual labels.
- **Attack, Decay and Release** are edited by dragging their boundary
  *vertical* lines. If a segment duration is zero, its boundaries overlap;
  a pointer-down on that overlap must move the **second** boundary when
  dragged, allowing recovery to a positive duration.
- **Sustain** is edited by dragging the *horizontal sustain-section line*.
  Preserve the currently valid Core sustain scalar semantics (finite,
  possibly less than zero or greater than one); the graph's vertical
  scaling/clipping should be defined and tested without silently
  normalizing the underlying value to [0,1].
- Changes should be reflected in the underlying document and revision
  tracking, with usable synchronization between graphic handles and
  any retained numerical controls. The reusable editor needs both
  document-view and embedded-inspector modes.

## FM instrument editor: first-class envelopes

- A user can add an **Envelope node** even when no envelope objects
  currently exist: do not display a create-first blocking message.
- Its reference dropdown starts with **no selected value** on creation.
  Place an italicized **New...** entry at the *top* of that dropdown.
  Choosing New... creates a new envelope object and makes it available
  for selection/assignment without leaving the FM instrument editor.
  Preserve a distinguishable unassigned/blank state.
- On selecting an Envelope node, its FM editor detail/inspector pane
  embeds the reusable graphical envelope editor. Its edits must mutate
  the same persistent envelope object visible in the Envelopes pane,
  not a copy. Avoid losing selection or draft state as catalogues refresh.
- The current `FmSynthEditorControl.AddEnvelope` refuses this operation
  when no `EnvelopeDefinition` exists, and its inspector only has an
  envelope selector; both are redesign targets.

## Unsaved-changes dialog and File -> Open

- The **Save changes?** dialog's buttons appear left-to-right:
  **Yes**, **No**, **Cancel**, in that order.
- Add platform-appropriate **accelerator keys** for all three choices;
  **Yes** must have `IsDefault = true` and **Cancel** must have
  `IsCancel = true`. Confirm that Enter, Escape, and each mnemonic
  actually produce Save / Discard / Cancel respectively.
- File -> Open must show its file picker **first**. A canceled picker
  (or one without a selected usable song) leaves the current document
  alone and **must not** prompt to save. Once a usable path has been
  chosen, run the existing unsaved-change guard *before* replacing the
  document. Keep the current document unchanged if the user cancels
  or if saving/loading fails. New and Exit retain their existing guards.
- Current `MainWindow.OpenDocumentAsync` checks the guard before
  `StorageProvider.OpenFilePickerAsync`; the ordering above is a
  requested change, not current behavior.

## Instrument Editor: implicit specifications and tone-table grid

The visual editor stops presenting tone specifications and tone table as
two separately edited lists. Present **one editable tone-table grid**;
manage the Core tone-specification list implicitly. Do not treat visual
temporary rows as saved musical data until they have an assigned Source.

### Row setup and ordering

- When an Instrument editor loads and whenever **Divisions** changes,
  initialize the table to a **blank entry row** at the top, followed
  by one row for each index in the regular pitch range, **highest pitch
  first**. The regular range ends at whichever comes first:
  **10 × Divisions** or **C-11**, **accounting for Offset**.
- Re-display existing tone specifications at their indexed rows.
  Every existing assigned index outside the regular ten-octave range
  receives an extra row **between the blank row and the regular range**,
  in descending index order. Shade these out-of-range rows with
  ARGB **40FF0000**.
- Keep all rows in descending index order except for the reserved top
  entry row, even after insertion/Divisions changes. Specify and
  regression-test the integer index/range derivation given the current
  `double Divisions` and `int Offset` Core fields; do not quietly
  round arbitrary user values or discard out-of-range assignments.
- **Delete** with grid focus removes the selected tone specification
  (and its effective mapping); avoid deleting the reserved blank entry
  row unintentionally.
- When the **top blank row loses focus**, if its index field contains
  a valid, nonnegative integer, migrate its edited cells to the matching
  indexed row, **overwriting any existing contents**. If the index lies
  outside the current rows, insert a corresponding extra row in reverse
  index order, shaded **40FF0000** when outside the core range. Reset
  the top entry row to blank for another insertion.
- Preserve visual row data with no selected Source **for the lifetime
  of the open Instrument editor**, even though such rows are not
  serialized into the Core tone table/specification list.

### Columns (exactly nine, in this order)

| # | Heading | Cell role |
|---|---|---|
| 1 | *(none)* | Tone index: blank/editable only in the top entry row; read-only in other rows |
| 2 | *(none)* | Closest nominal note plus `+` / `-` adjustment; read-only, blank in the top row |
| 3 | **Source** | Dropdown selecting the sound source |
| 4 | **Pitch** | Editable pitch multiplier **expressed as a logarithmic offset** |
| 5 | *(none)* | Pitch-multiplied closest-note dropdown, blank in the top row; includes notes in the multiplier range **±0.3** |
| 6 | **Volume** | Volume-envelope dropdown |
| 7 | **Pitch** | Pitch-envelope dropdown |
| 8 | **Panning** | Panning-envelope dropdown |
| 9 | **Filter** | Filter-envelope dropdown |

All cell dropdowns **except the pitch-multiplied closest-note control**
have an empty/clearing entry at the top. The pitch-multiplied note
selector has no blank clearing entry.

### Closest-note notation and pitch coupling

- **Closest note** means nearest standard musical note, decorated with
  an adjustment of zero or more repeated `+` or `-` characters.
  The number of characters follows the requested rule: the floor of
  the difference between the actual scale position and the closest
  standard tone divided by **Divisions**. Retain the exact convention
  expressed by the following 48-divisions-per-octave example when
  implementing/testing the integer arithmetic:
  `C-1, C-1+, C-1++, C#-1-, C#-1, C#-1+, C#-1++, D-1-, ...,
  B-1-, B-1, B-1+, B-1++`.
- The second closest-note column (column 5) represents the nominal
  note multiplied by the row's pitch multiplier.
  When a user edits the **Pitch multiplier** value, automatically select
  the nearest choice in column 5 **without altering the entered value
  if the match is inexact**.
- When the user **actively selects a new value in column 5**,
  snap/write the Pitch multiplier to the *exact* multiplier represented
  by that note choice. Do **not** feed back an automatic selection change
  into a pitch update; the directions are intentionally asymmetric.
- Column 5 offers closest-note options corresponding to the supplied
  pitch-multiplier range **±0.3**; define the precise logarithmic
  coordinate and option-generation boundaries in tests when implementing
  this control, preserving the user's semitone/sub-note convention.

### Persistence and editing lifecycle

- Changes to populated grid rows apply automatically to the live
  Instrument document, without an Apply/Save-table button.
- Core currently stores `ToneSpecifications` and `ToneTable`
  separately and permits multiple table entries to share a specification.
  The redesign is **implicitly managed specifications**: update the
  internal mapping consistently, including reuse/renumbering and
  deletion, without stale or wrong-index references. Preserve valid
  pitch multipliers and all four envelope overrides.
- A row without a **Source** is *not* saved, even if other columns hold
  values. Preserve those unsaved visual drafts while this editor remains
  open, including ordinary grid refreshes and focus changes. Adding
  a Source later makes the row persistent; clearing a Source removes its
  persisted mapping while retaining its visual fields until the editor
  closes. On reopening, only persisted rows are reconstructed.
- Changes must advance the existing document/audio revisions
  appropriately and must retain snapshot isolation for already-playing
  audio.

## Suggested implementation order

1. Unsaved-changes dialog and file-picker ordering (contained, testable).
2. Pattern playback-follow checkbox, hotkeys and viewport behavior.
3. Patch/Patches labels (only generalized source contexts).
4. First-class Envelopes tree/pane and persistence/placement, then the
   reusable ADSR graph.
5. FM Envelope node creation/embedded editor.
6. Tone-table grid projection/model tests, draft-row persistence and
   implicit Core specifications; then keyboard/edit/pitch coupling
   details and a final integration pass.

The order is a recommendation, not a change to the requested behavior.
