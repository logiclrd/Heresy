# Planned UI polish and instrument/editor redesign (October 10, 2026)

Status: **living implementation and design checklist**. Sections marked
implemented below describe completed milestones; unmarked instrument-grid
and FM-inspector requirements remain future work. Core
`InstrumentDefinition` still has separate `ToneSpecifications` and
`ToneTable`, while `AdsrEnvelopeDefinition` stores TimeSpan
Attack/Decay/Release and an unrestricted finite SustainLevel.
The current document tree has **five** section roots.

## Pattern editor: optional playback-follow — implemented

- The Pattern editor now has an initially enabled **Follow** checkbox
  controlling playback viewport tracking, not the transport state.
- The three keyboard shortcuts are **NumPad Period/Decimal**
  (Renoise alternative), **Ctrl+F** (OpenMPT), and **Scroll Lock**
  (MilkyTracker). They operate on the same checkbox state, ahead of
  tracker-cell editing, with repeat-key suppression. The shortcut
  uses physical `NumPadDecimal` independent of keyboard locale.
  **Backtick remains Note Off** and **regular Period** remains a normal
  tracker editing key. This intentionally supersedes the original
  conflicting backtick-follow design.
- When enabled, a new playback-highlighted row scrolls **as close to
  the vertical center as the scroll extent allows**, clamping at document
  edges. When disabled, existing playback highlighting remains visible
  without playback-driven scrolling. Turning Follow on while a row is
  already highlighted also requests centering.
- Existing `PatternEditorContext` mapping resolves the highlighted
  display rows, including per-Sequence-entry placement and repeated
  Pattern sources. Measured row-header geometry accounts for variable
  Sequence segment-header heights; when multiple rows match, the
  candidate nearest the current viewport center is followed. Horizontal
  scroll, edit cursor and focus remain unchanged.
- `PatternPlaybackFollowTests` cover modifier-exact shortcuts, numpad
  text suppression, ordinary Period/Backquote exclusion, scroll
  centering/clamping and repeated-order mapping. Actual window-manager
  focus and viewport behavior can additionally be smoke-tested on desktop.

## Document panes and vocabulary — implemented

- **Envelopes** now has a first-class document-tree section and dedicated
  pane. The top row has two equal-width panes (Sequences, Patterns), and
  the bottom row three equal-width panes (Patches, Envelopes,
  Instruments). The layout uses six star columns with spans of 3/3
  above and 2/2/2 below, and wraps the pane action buttons.
- General UI references to the former mixed Sample/FM section now say
  **Patches**; sample import, PCM formats, waveform and loop editing
  continue to use Sample where the object really is PCM-backed.
  The enum `SongTreeSection.Samples` is retained for existing code,
  and the persisted top-level folder name becomes `Patches`.
- New Envelopes are placed in their section, and default activation
  opens the existing Envelope editor. Cross-section tree movements remain
  prohibited. The former four-root version-1 tree is accepted on load:
  its Envelopes (including tombstones) move from Instruments into the
  new root with any containing folder hierarchy mirrored. Non-envelope
  objects retain their placements. No ObjectId changes, audio edits or
  version bump occur; saves emit five roots. See
  [document-panes.md](document-panes.md).

## Graphical ADSR envelope editor — implemented

- The reusable `AdsrEnvelopeGraphControl` is backed directly by the
  *same* live `AdsrEnvelopeDefinition` as the numeric Envelope editor;
  it renders Attack, Decay, held Sustain and Release as an editable line.
- The graph's right-hand edge has **Note Volume** at reference Y=1,
  **0** at the lower reference edge and a **Sustain** annotation
  tracking the line vertically and reporting its current scalar.
- Attack, Decay and Release are edited by dragging **vertical endpoint
  handles**. Their marker caps occupy separate Y lanes, so when a
  segment duration is zero and the lines coincide, dragging its
  *second/ending* line makes the duration nonzero again, even when both
  Attack and Decay are zero. On an unmarked overlap, the later
  endpoint has precedence.
- Sustain is edited by dragging its **horizontal segment**. Graph
  Y=0..1 is a visual reference only, not a new Core parameter
  constraint. Sustain outside this interval is visually clipped to
  the corresponding graph boundary and explicitly labelled
  `(clipped)` with the **actual** value. Pointer dragging outside
  the plot can edit those legal negative/above-unity values.
- Drags preview the curve without mutating the song. Pointer release
  commits the result via `EnvelopeDocumentEditor.UpdateAdsrEnvelope`
  (correct live object, audio/document revisions and no-op semantics);
  lost capture cancels the preview and reverts. Numeric Apply updates
  the graph, and committed graphical edits immediately update all four
  numeric fields. A live document Changed subscription resynchronizes
  the reusable graph when external changes are made; it is removed
  on detachment to prevent event leaks.
- The testable `AdsrGraphGeometry` covers Y mapping, nearest handles,
  zero-duration overlaps, sustain line hits, unrestricted values,
  duration clamping and preserved fields; GUI appearance and pointer
  capture still merit a manual desktop smoke test.
- Embedding **this same control** into the FM synth editor's Envelope
  node inspector, along with the New... dropdown/create path, is the
  separate **next** TODO. See [envelope-graph.md](envelope-graph.md).

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
**Implemented:** `UnsavedChangesDialogActions` supplies the ordered
`_Yes`, `_No`, `_Cancel` buttons (Alt+Y / Alt+N / Alt+C) and
`DialogActionLayout.ConfigureButtons` assigns Yes/Cancel the
Enter/Escape keyboard roles. `DocumentOpenWorkflow.TryOpenAsync` now
picks a usable path before evaluating the unmodified dirty-document
guard; a canceled picker leaves the dirty document untouched without
asking to save. `DocumentWorkspace.Open` validates/decode-loads the
replacement and resolves JSON path mode before swapping its active
document or save baseline. The original New/Exit workflows still
guard before their respective actions. These changes are backed by
headless dialog-action and picker/guard/transaction regression tests.

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
  `C-1, C-1+, C-1++, C#-1-, C#-1, C#-1+, C#-1++,
  D-1-, D-1, D-1+, D-1++, D#-1-, D#-1, D#-1+, D#-1++,
  E-1-, E-1, E-1+, E-1++, F-1-, F-1, F-1+, F-1++,
  F#-1-, F#-1, F#-1+, F#-1++, G-1-, G-1, G-1+, G-1++,
  G#-1-, G#-1, G#-1+, G#-1++, A-1-, A-1, A-1+, A-1++,
  A#-1-, A#-1, A#-1+, A#-1++, B-1-, B-1, B-1+, B-1++`.
  This complete example is an explicit acceptance fixture for note naming,
  including the negative adjustment immediately below sharp notes.
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
