# User-interface authoring and workflow contracts

This is the detailed **implemented** UI contract preserved from the former
root README (October 10, 2026), not an open implementation checklist.
For focused controls see [the documentation index](README.md) and
[document panes](document-panes.md); for genuinely open items see
[todo.md](todo.md). The [October 10 redesign snapshot](ui-editor-redesign.md)
is historical; source code and current tests win over dated prose.


## Document identity and six panes

The UI is a projection/editor of the semantic Core model rather than a second
song model. `DocumentWorkspace` owns the one active `SongDocument` plus its
save baseline, while `SongTreeItemViewModel` resolves tree object IDs to live
names and falls back through tombstones to raw IDs for broken references.
Tree-only reorganization uses `SongTreeEditor` and advances `DocumentRevision`
without advancing `AudioRevision`; moving a node never changes its ObjectId.

The document tree has **six** fixed top-level sections, persisted in
visual row-major order: Sequences, Patterns, Patches, Filters, Envelopes
and Instruments. The top row has two half-width panes (Sequences and
Patterns); the bottom has four quarter-width panes (Patches, Filters,
Envelopes and Instruments). The shared **four-column** grid spans two
columns for each top pane and one for each bottom pane. Pane action
buttons wrap rather than overflow. The fixed root nodes are not shown
within their panes because each pane represents one section subtree.

**Patches** is the general UI label for the section containing both
decoded PCM Samples and Synths. The user-visible New/Edit/Import names now say **Synth**, including `+ Synth`, `+ Import Synths`, `Edit Synth...`, and the corresponding dialogs and status messages. Existing saved Synth objects retain the same FM implementation type and IDs. The Core section enum remains
`SongTreeSection.Samples` for compatibility. UI labels tied strictly
to PCM data (sample import, sample file formats, sample waveform and
loop editing) still say **Sample**. New Envelopes receive canonical
placements in the first-class Envelopes section rather than under
Instruments. Folders can be reorganized within a section, not moved
between sections.

Legacy version-1 `.hm.json` and `.hm` documents load into the
current six-section tree **without a song format version change**.
Four-section trees (Sequences, Patterns, Instruments, Samples) move
Envelope placements/tombstones out of Instruments while preserving
folder paths and object IDs, then rename Samples to Patches. Five-section
trees (Sequences, Patterns, Patches, Envelopes, Instruments) gain the
Filters section between Patches and Envelopes. Current six-section trees
load directly. Saving writes six sections, and migration itself does not
increment audio or document revisions. See
[document panes](document-panes.md).

## Held-key input and nested X11 servers

The Synth **Test** area, Sample **Test** area, Pattern editor's momentary
Shift piano preview, and playback-follow toggle use **physical** held-key
tracking. On X11 servers without detectable autorepeat, including some
Xephyr configurations, a repeating key can generate a synthetic
`KeyUp`/`KeyDown` pair. An immediate key-up used to release notes or rearm
a shortcut would therefore retrigger the held key on every repeat.

These four held-key paths defer individual physical key releases for a
**40 ms grace interval**. A new keydown during that interval cancels the
pending release and is ignored as a repeat. A release without a matching
keydown then sends Note Off (Synth/Pattern preview), re-enables a Sample
Test piano key for another distinct note, or rearms the playback-follow
toggle. Multiple held keys are tracked separately. Focus loss, window
deactivation, and editor detachment discard pending releases and perform
the previous immediate cleanup; the Sample dialog intentionally lets
released sample notes continue sounding until explicitly stopped,
displaced, or finished.

Ordinary tracker note entry, editing, text fields and navigation retain
their normal key-repeat behavior. Confirmed physical key releases incur
at most roughly 40–50 ms of extra latency (grace interval plus UI timer
scheduling); this is deliberate to distinguish X11 synthetic repeat
pairs without requiring platform-specific Xlib hooks.

## Synth oscillator Curvature (authoring and persistence)

A Synth oscillator has an independent **Curvature** numeric parameter, shown
immediately beneath **Waveform** in the graph inspector. Its default is `0`,
including when opening version-1 songs that predate the parameter. The editor
accepts finite values from `-32` through `+32`, using invariant-culture
numeric entry and the existing Enter/focus-loss commit and Escape draft-revert
behavior. Each successful change replaces that oscillator's immutable graph
node and advances the document/audio revision; no-op edits do not.

**Oscillator waveform preview:** Waveform and Curvature controls occupy the
left cell of a two-column inspector row. The right cell contains a
resizable graph of exactly three cycles of the selected oscillator's
*normalized waveform*, over continuous phase `[0.5, 3.5]`; the left/right
edges correspond to phases 0.5/3.5 and integer-phase cycle resets occur
at 1, 2, and 3. The graph uses the renderer's shared waveform evaluator
and the same prepared Curvature transfer function; it shows the wave
*before* `Vmin`/`Vmax` scaling. Both Waveform selection and committed
Curvature edits update the graph immediately without rebuilding the
inspector or discarding another field's draft. A rejected numeric edit
does not alter the preview. The graph redraws at the current layout width
when its inspector is resized; it is an authoring aid, not a live PCM
oscilloscope.

Curvature is saved on the oscillator node as `curvature` and retained by
graph connections/disconnections, cross-song Synth imports, and playback
snapshots without a persisted schema-version increment. The **sample-level
exponential shaping** is now applied by the prepared oscillator renderer:
nonzero Curvature shapes the normalized waveform before Vmin/Vmax, while
zero Curvature preserves the original PCM path. See the
[Synth sound source contract](synth-sound-source.md) for its transfer function,
lookup-table approximation, and realtime-performance guarantees.

## FM oscillator frequency and Constant mode

Every oscillator's **Frequency Hz** is interpreted relative to the requested
note by default: its frequency scales with the note's pitch, playback-speed
pitch multiplier, and live pitch trajectory. This is appropriate for pitched
carriers and pitched modulators, and is the default for newly created patches.

The **Constant** checkbox sits immediately below the selected oscillator's
**Frequency Hz** field in the FM graph inspector. When checked, that
oscillator ignores the note-related pitch multiplier and instead advances at
its configured frequency in real time. This permits fixed-rate vibrato,
tremolo, and other LFO sources that don't accelerate when a higher note is
played. The checkbox applies to the selected oscillator only; other
oscillators in the same FM graph retain their independent settings.

A connected **Multiplier input** remains active in Constant mode, including
the **Exponential multiplier (semitones)** interpretation. Constant
disconnects the oscillator from the *note pitch*, not from deliberate
FM graph modulation. Changes immediately replace the persistent graph and
advance audio/document revision, and the flag is preserved across song
save/load, snapshotting, node connections, disconnections, and synth imports.

## FM graph editing

FM synth graph connections are drawn as orthogonal routes from producer to
consumer, with a small arrowhead **only at the consuming/input end**. Hovering
over a node border reveals connection ports: green output handles on the
right, blue input handles on the left, and an additional gold input handle on
operators for appending an input. Dragging between an output and a compatible
input (in either direction) creates the semantic connection; dropping elsewhere
does not change the graph. An oscillator has one replaceable multiplier input,
while operators can replace a selected input or append another ordered input.
Cycle detection and all other graph validation occur before committing, and
invalid drags leave the graph, revisions and routing hints unchanged.

Removing a non-output FM node disconnects every consumer atomically: oscillator
multiplier inputs become unset while retaining the oscillator settings, and
all matching operands are removed from operators (including repeated inputs).
An operator with no remaining inputs is converted into a zero-valued constant
with the same node ID, preserving its location, output selection and any
downstream dependencies without relaxing the Core requirement that operators
have at least one input. Surviving connection waypoints are remapped to their
new operator input indices. The deletion advances the audio/document revision
once. The selected output node still cannot be removed until another output
is selected.

Clicking a graph node also focuses the graph canvas for keyboard shortcuts.
With a node selected, **Delete** invokes the same **Remove node** action as
its inspector button, including disconnected consumers, one audio/document
revision, and selection moving to the remaining output node. Trying to delete
the output node displays the same refusal as the button. Delete with no node
selected or with keyboard modifiers is ignored. When a parameter text field
has focus, Delete continues editing that text and cannot remove a graph node.

Connection routes terminate at the exact input-port positions, including the
individual inputs of an operator; backward connections travel around an
exterior lane to reach the fixed port sides. Arrowheads follow the last
nonzero segment and scale down for short segments. Explicit routing waypoints
are retained and the route finishes with a short horizontal approach to the
consumer. Editor-only node positions and routing hints never alter the
semantic audio graph.

Connection waypoints are edited directly on the canvas. Pointer hit-testing
uses a three-physical-pixel tolerance on either side of the rendered wire,
with the display scaling accounted for. Dragging a wire creates a waypoint at
the clicked segment's position, inserted before or after existing waypoints
according to the location along the actual orthogonal route. A small gold
handle marks every waypoint; drag it to move it, or double-click it to delete
it. The path is previewed live during drag and persisted only on release,
and a simple click without moving does not add a waypoint. Deleting the final
waypoint clears the routing hint, restoring automatic routing. The old raw
text waypoint editor and its buttons have been removed; these layout-only
edits never advance the audio revision.

The FM editor and Sample editor use the **same focusable Test control**, the
layout-independent tracker piano (`Z S X D C` and higher rows), and the
keypad `*`/`/` octave shortcuts (initially octave 4, bounded to 0–8).
With **Caps Lock OFF**, each note is held until its own confirmed key release
and then receives **Note Off**. With **Caps Lock ON**, notes remain playing
after key release, ending naturally or when F8 explicitly cuts the preview;
each latched note start also **cuts any existing note fewer than three
semitones away**, including an identical pitch and across octave boundaries.
The rule applies equally to Sample and Synth, not only looping samples.
A note's release-on-keyup mode is captured at note start; changing Caps Lock
while holding a note does not retroactively change that note's lifetime.
Every press has its own identity and a unique virtual voice. Synthetic X11
repeat events cannot retrigger a held key; F8 doesn't rearm any keys still
physically held. Moving focus away or deactivating the window releases only
momentary notes, leaving latched notes alive; closing the editor cuts all
of its remaining preview voices. Ordinary song data remains unmodified.

The Test **keyboard controller** is separate from its **voice controller**.
The input emits NoteOn with pitch, press identity and `SuppressNeighbours`,
NoteOff by press identity, or StopAll. No modifier or lock-state query reaches
the voice layer. The voice layer handles unique voice IDs, suppression,
Note Off, Note Cut, release-tail tracking and Sample loop-edit restarts.
Both editors reuse the same control and voice controller, with an adapter
that compiles ordered events to the existing live-note transport. The mode
label displays Hold or Latch.

Native Caps Lock query uses Win32 `GetKeyState(VK_CAPITAL)`, CoreGraphics
`CGEventSourceFlagsState`, or X11 `XkbGetState` against the active `DISPLAY`
(which includes Xephyr). Native Wayland does not provide a generally
queryable global lock-state API outside the compositor's keyboard protocol;
if a native query is unavailable the Test control reports this visibly,
starts in Hold, and tracks subsequent Caps Lock presses locally while
focused. The local fallback cannot discover an already-enabled Caps Lock
state until the user toggles it inside Test.

FM node-parameter fields commit individually on Enter or loss of keyboard
focus; dropdowns and checkboxes commit as soon as their selection changes.
There is no node-parameter Apply button. Each text field tracks its own latest
successfully committed value. Escape restores only the current uncommitted
draft: editing a field, tabbing/clicking elsewhere to commit, then returning
and pressing Escape does **not** undo that committed edit. Invalid values
report a validation error without changing the graph; invalid drafts are
reverted on focus loss. Parameter edits use the current immutable node value
when constructing the replacement, so editing another parameter does not
overwrite unrelated committed settings. Ordinary commits refresh the graph
without rebuilding the inspector or disrupting focus.


## FM editor preview release lifecycle

With Caps Lock off, Test key-up (or loss of Test focus/window focus)
emits **Note Off**, not **Note Cut**. With Caps Lock on, the key-up does
not issue Note Off; the same underlying FM envelope and finite-tail rules
still apply to latched Synth voices. FM synths calculate their Envelope
nodes *inside the graph*, so the sound now reports a deterministic end
when the output graph is proven silent after its embedded envelope tail.
A bare Envelope output is finite when its envelope declares a release
bound. Multiplication becomes silent when **any** operand is guaranteed
zero; addition/minimum/maximum require **all** inputs to become zero.
An Envelope used only as an oscillator's frequency input does **not**
prove its output silent. Live audition therefore preserves audible
release tails for envelope-gated FM sounds without retaining indefinite
ungated oscillators: unbounded FM sources still use the existing live
virtual-voice safety cut. Ordinary Pattern playback continues to use
its existing Note Off semantics without that live-preview fallback.

## Sample waveform loops and note audition

In the Sample editor, the decoded waveform supports both dragging an
existing loop start/end handle and dragging across the waveform to select a
new interval. A new selection uses the smaller frame position as its start
regardless of drag direction, and a region must contain at least one decoded
frame. When the current Loop Mode is None, finishing a selection sets it
to Forward; existing Forward/PingPong modes remain unchanged. The waveform
shows the provisional selection during the drag. Releasing the mouse commits
the loop to the document and synchronizes the start/end text fields.

Sample Test has the same Hold/Latch Caps Lock contract as Synth Test,
including press-specific Note Off, neighbour suppression when latched,
and independent voices even when repeatedly striking the same key. A
non-looping sample can end naturally while the Test control retains its
voice identity for future note cuts; held Sample keys are not treated
as automatically persistent. The adjacent-note rule is driven by the
NoteOn event's policy flag, never by the Sample controller reading
Caps Lock or Loop Mode.

Editing either loop-frame textbox and leaving focus commits the loop,
as does waveform mouse release; in both cases the Sample editor restarts
its tracked continuing voices at their *original* pitches using updated
loop metadata and their existing distinct voice IDs. Notes are not
restarted on provisional mouse movement. F8 stops playback, but does not falsely rearm still-held physical keys.
Deactivating the dialog releases its momentary notes; its latched voices
continue while the user edits, and closing it cuts all remaining voices. Sample metadata,
not a cached preview-only loop, remains the source of truth for every
restart.

## File menu and unsaved changes

The desktop File menu offers New (`Ctrl+N`), Open (`Ctrl+O`), Save
(`Ctrl+S`) and Exit (`Ctrl+Q`). These shortcuts are handled by the window
only after the focused control has had an opportunity to handle its key event,
so pattern-editor key bindings take precedence on conflicts. File -> Exit uses
the normal window-close path: when the document is dirty, the existing
save/discard/cancel confirmation can prevent closing, just as it does for New
and Open.

Dialog action rows are anchored at the lower-right of their windows using a
flexible content area and a bottom button row, including in resizable import
and sample dialogs. Standard accept/confirm buttons are the Enter defaults,
while Cancel (or the Sample editor's Close button) responds to Escape. In the
unsaved-changes confirmation, buttons are arranged **Yes / No / Cancel**,
with Alt+Y / Alt+N / Alt+C access keys. Yes is the Enter default,
Cancel is the Escape action, and No explicitly discards unsaved changes.
File → Open now opens its picker **before** prompting: canceling the
picker does not prompt to save; a chosen usable path is only loaded
after the existing save/discard/cancel guard permits replacement.
A failed load preserves the current authoring document and save mode.

## Pattern and Sequence views

Opening or creating a data pattern switches the main workspace into pattern
mode rather than opening a modal editor. The pattern grid edits semantic note
(empty/start/off/cut), Source, Volume and effect columns directly; Start notes
carry pitch/playback-speed/mixdown while Source is independent pattern-cell data.
The editor exposes row/channel dimensions plus minor/major row-highlight
intervals and warns before shrinking dimensions when populated cells would be
discarded. The Back control is fixed at the left of the editor header; the
Pattern name and toolbar controls through **Apply layout** are in a horizontally
scrollable area with an automatic scrollbar (hidden when there is no overflow).
The scrollable region always measures an extra bottom gutter for the scrollbar,
including when it is hidden, so narrow windows do not cover the lower halves
of inputs and resizing does not change the header height.
Double-clicking the displayed Pattern name overlays an editable text field
containing its current name. Losing focus or pressing Enter commits a nonblank
rename through `SongTreeEditor.RenameObject` (synchronizing all document-tree
placements without an audio revision); Esc cancels and restores the label.
In Sequence projection mode, renaming edits the underlying current Pattern
rather than renaming the Sequence or changing its order.

Pattern row divisions are presentation rather than typography: major/minor
rows use translucent background bands instead of bold text. The shared
`UserInterfaceConfiguration` currently defaults major rows to ARGB
`80808080` and minor rows to `40808080`, allowing the neutral grey to blend
lighter in dark themes and darker in light themes.

Opening or creating a data sequence likewise switches the main workspace into
an arrangement-list editor. Sequence entries remain ordered `(PatternId,
StartRow)` references rather than duplicating pattern data. The editor can add
live data or script patterns, replace an entry's pattern, edit its non-negative
start row, reorder/remove entries, and explicitly mark the sequence as the
song's `RootSequenceId`. Creating a sequence does not implicitly make it root.
Broken pattern references are preserved and projected through tombstones; their
start row remains editable and they can be repaired by selecting a live pattern.

There is only one tracker-pattern editor implementation. When opened for a
single `DataPatternDefinition`, its display-row context maps directly to that
pattern. When opened from a data-sequence order, the same `PatternEditorControl`
is instead given a sequence context plus the order that should receive initial
focus. That context flattens the sequence's declared data-pattern entries into
adjacent display rows, respecting each entry's `StartRow`, while every display
row resolves back to the original live `(DataPatternDefinition, local row)`.
Scrolling or cursor paint-down can therefore cross a pattern boundary directly;
editing never creates sequence-local copies. Repeated references to the same
pattern are multiple projections of the same object, so an edit is refreshed in
every visible occurrence. Script-pattern and missing-pattern orders remain
visible as non-editable segment separators rather than being silently hidden.
The sequence projection follows declared arrangement order; it does not attempt
to predict runtime Bxx/Cxx jumps or pattern-break control flow, which remains a
Core sequencing concern. The Back control becomes **← Sequence** and returns to
the arrangement list.

During realtime playback, row highlighting and Follow track the Pattern
being played directly, or the current direct Pattern order when playing a
Sequence. If a note invokes a flattened Pattern or Sequence sound source,
its internally generated rows do **not** replace the parent editor's playback
position. The nested music still runs on the shared clock, including its
independent timing and effects; this distinction affects UI tracking only.

Script patterns and script sequences can also be created directly from their
respective document panes and are edited by one shared main-workspace script
source editor. Script-pattern authoring exposes the same row/channel and
minor/major-highlight layout values as other pattern definitions; script
sequences can be explicitly selected as the song root. Opening a script-pattern
order from a data-sequence arrangement opens that script source editor with
**← Sequence** navigation rather than creating a second tracker representation.

## Script editor and diagnostics

Persisted script source remains ordinary restricted-C# text. Script editing now
uses AvaloniaEdit. The desktop `App.Initialize()` must register the
AvaloniaEdit Fluent style resource
(`avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`) after the
application's Fluent theme; the general Avalonia theme does not supply
AvaloniaEdit's text-control template. Omitting this style produces a blank
reserved editor area despite the source control being present. A desktop
initialization regression checks that the actual App registers the theme.
Its `TextDocument` always contains the canonical source
including literal `_O(id)` expressions. Roslyn semantic analysis supplies the
raw source spans for actual Heresy object lookups; comments, string lookalikes
and shadowed helper calls do not become references. A custom
`VisualLineElementGenerator` replaces each semantic lookup only in the visual
line with one named `FormattedTextElement`. That visual element has visual
length 1 while consuming the complete raw `_O(id)` document span, so the object
name behaves as one editor token without introducing a second editable string
or a second coordinate system. AvaloniaEdit therefore keeps native caret,
selection, clipboard and undo behavior against valid raw restricted C#.
Current live names are projected without rewriting source, tombstones retain
their last-known names, and unresolved references display their canonical raw
ID.

Live authoring analysis also retains an incremental Roslyn syntax snapshot.
Each text change uses `SyntaxTree.WithChangedText`, allowing Roslyn to reuse
unchanged syntax while still returning an error-tolerant tree and diagnostics
for incomplete code. Valid semantic references and highlighting elsewhere in
the document remain available while another expression is being typed.

The script editor now builds syntax-highlighting spans directly from that same
Roslyn tree. Keywords (including contextual keyword spellings), string/character
and raw-string literals, interpolated string text, numeric literals, comments,
disabled/preprocessor text, and semantic object references receive distinct
highlight kinds. Comment/directive and object-reference spans take precedence
over nested lexical spans. In particular, the complete raw `_O(id)` source span
is emitted as one `ObjectReference` highlight rather than separate identifier,
punctuation and number spans. A `DocumentColorizingTransformer` clips those raw
spans to each `DocumentLine` and applies them with `ChangeLinePart` after
AvaloniaEdit has run the object-reference element generator. Consequently a
generated one-column object-name element is styled through the exact Roslyn
source span that produced it, while ordinary source remains normal
`VisualLineText`. Multi-line comments/raw strings are naturally colored a line
at a time.

Compiler diagnostics now use the same raw-coordinate model for in-editor
adornments. Diagnostic spans are normalized to the current document before
rendering; non-empty spans retain their source range, while zero-length Roslyn
diagnostics (such as an expected token at end-of-file) expand to a visible
one-character anchor when the document is non-empty. An AvaloniaEdit
`IBackgroundRenderer` asks `BackgroundGeometryBuilder` for the visual rectangles
corresponding to each raw span and draws severity-colored wavy underlines on the
text layer. Because the geometry builder resolves through the completed visual
line, a diagnostic covering a raw `_O(id)` span automatically underlines the
single generated object-name element rather than assuming one visual column per
source character. Error, warning and informational diagnostics use distinct
marker colors.

Diagnostic markers also drive hover help and navigation without invoking Roslyn
again. Pointer movement is converted by AvaloniaEdit from the rendered editor
position back to a raw `TextLocation`/document offset, then the current marker
catalog is queried for diagnostics covering that offset. The lookup is tolerant
of the end boundary of a diagnostic span so both halves of a generated
one-column object-reference element map back to the same hover. Overlapping
diagnostics are aggregated in severity order and shown in a pointer-positioned
tooltip containing severity, diagnostic code and message. The tooltip is cached
while the same diagnostic set remains under the pointer and is cleared
immediately when analysis refreshes or the pointer leaves the editor.

The persistent diagnostics list below the editor renders each diagnostic as a
clickable row. Activating one maps its *original* Roslyn span back into the
current AvaloniaEdit document, selects that range, moves the caret to its start,
scrolls the source location into view and focuses the editor. Navigation
deliberately uses the original span rather than the normalized visual-marker
span: a zero-width diagnostic at end-of-file therefore navigates to EOF even
though its squiggle is anchored to the preceding character for visibility.
Out-of-range stale spans are clamped to the current document. Object-reference
summaries remain non-interactive informational rows beneath the diagnostics.
The shared script editor continues to run the full restricted-C# compiler
validation without assembly emission/loading.

Core remains Roslyn-free through `IScriptObjectReferenceAnalyzer`. With no
analyzer, `SongReferenceAnalyzer` retains the conservative opaque-script
fallback. The authoring workspace supplies the Roslyn adapter for Save/Save As,
so persistence can retain tombstones referenced by real `_O(id)` expressions
while pruning tombstones mentioned only in comments or strings. If analysis is
unreliable because of syntax/reference diagnostics, the opaque safety fallback
remains active and tombstones are conservatively retained.

Script Pattern and Sequence definitions use the same incremental recursive
timeline as data-driven objects. Restricted-C# scripts are compiled through
the Roslyn scripting boundary into invocation-local cooperative iterators,
not eagerly evaluated into a full-song `NoteSchedule`. Pattern scripts
produce raw `NoteEvent` steps consumed by the common timing/effect processor;
Sequence scripts select the next order via `GetSequenceEntry(absoluteIndex,
sequenceIndex, previousSequenceIndex)` and may continue indefinitely.
The helper surfaces include `_O(id)`, `Note`, `Off`, `Cut`, `Tempo`,
`Speed`, `Play` (for Sequences), and deterministic `Random`.
`System.Math` is permitted; disallowed framework access, allocation,
async/exception control and other unsupported language features are
rejected with scripting diagnostics. Script loops yield cooperative
progress checkpoints and retain resource/iteration budgets so an
unbounded source need not block rendering. Generated state and seeded
randomness are scoped to an invocation, allowing deterministic
reconstruction when private sources are rewound.

The current song execution bridge is
`PreparedIncrementalPlaybackFactory`, not the removed eager
`SongScheduleCompiler`. It supports root or explicitly requested
Pattern/Sequence invocations with order/row starting positions, and
prepares restricted-C# generators for lazy invocation. A script is
evaluated only when musical progression reaches it; the resulting
event stream stays incremental throughout realtime playback and
export. Errors from invoked scripts remain surfaced through the
compiler/runtime diagnostic paths, and script layout/source edits
remain audio-affecting.

## Instrument tone grid

The Instruments pane creates `InstrumentDefinition` objects and now uses
**one unified eleven-column tone grid**, with implicitly managed
ToneSpecifications and ToneTable references. The additional per-tone
NNA and Filter fields are described in
[instrument tone grid](instrument-tone-grid-model.md). Divisions and Offset are
the pitch-to-index lookup parameters used by the renderer. The
top blank row creates/overwrites a mapping when the entire row loses
focus; rows descend from the highest regular pitch, with additional
mapped out-of-range indices highlighted translucent red (ARGB
`40FF0000`). Select a row and press Delete to remove its mapping.
Each mapped row chooses any live sound-producing Source (including
sample, FM synthesizer, instrument, pattern and sequence), one
logarithmic **Pitch** offset, and Volume, Pitch, Panning and Filter
envelope overrides.

The two nearest-note columns are derived from the **instrument's actual
Divisions and Offset**. The closest conventional 12-note chromatic
pitch is named (C, C♯, etc.), followed by one `+` or `-` for
**each instrument index step** away from that chromatic note's nominal
index. This notation is not tied to 48 divisions per octave.
Chromatic ties favor the lower pitch. The editable Pitch column is
`log2(pitch multiplier)`. Its adjacent note dropdown offers exact
instrument-grid pitches within ±0.3 of that logarithmic offset.
Typing a Pitch offset automatically highlights the nearest note
**without changing the entered value**; choosing a note explicitly
snaps the multiplier to the exact value.

Source and envelope selectors start with a clearing option. A row
without a Source keeps its pitch/envelope edits **only while the current
editor instance remains open**; it is not persisted to the song.
Assigning a Source automatically publishes the row to Core; removing
it restores the silent mapping while retaining the editable draft.
Shared Core tone specifications are copy-on-write, and equivalent
active specifications are interned when publishing, avoiding stale
indices and unintended modifications to other rows. Playback's
frozen document snapshots remain isolated from subsequent edits.
See [tone-grid implementation](instrument-tone-grid-model.md) and
[historical redesign snapshot](ui-editor-redesign.md).

## Node envelopes and FM envelope editing

Node envelopes are first-class shared objects in the **Envelopes** pane.
Ordinary New Envelope creates a `NodeEnvelopeDefinition` with a piecewise
linear sequence of time/value nodes, indexed sustain start and end points,
and Forward/PingPong looping. Script Envelopes remain a distinct supported
authoring type. The standalone Envelope editor and the FM synth's Envelope-node
inspector both use the same **interactive node graph** and auxiliary
node-list editor for precision.

The node graph displays connected linear segments, faint vertical
guides and the sustain band at ARGB `#40808080` (25% gray opacity).
None mode has no sustain band or looping. Vmin/Vmax controls define
a persistent, fixed vertical scale with 5% padding above and below,
independent of actual node values. Click within 4 pixels of a node to select
it; the selected node gets two small brackets. Dragging a node edits
its time and value (without crossing neighbours), while the time of
node zero remains fixed at zero. Double-click a node to delete it,
or double-click a line to insert a new node at its closest projected
point. Deleting the first node re-bases the remaining times to zero;
the last surviving node cannot be deleted. All operations commit
atomically through the shared envelope document revision path.
The sustain highlight uses `#40808080` and its endpoint guides are
3 pixels thick, versus 1 pixel for ordinary guides. Even a single-node
sustain is visible. Each vertical guide has a 5-pixel-wide drag target
(2 pixels to either side). Hover over an endpoint shows `SizeWestEast`,
and hover over another guide shows `Ibeam`. Cursor shape stays fixed
during capture. Dragging either boundary moves only that boundary,
crossing and reordering the two indices when necessary. Dragging any
non-endpoint guide—inside or outside the sustain region—defines
a new interval in either direction; self-drags do nothing. Double-clicking any vertical guide away from
its node sets a single-node sustain, whereas double-clicking the dot
deletes the node. Sustain drags preview before a one-time commit on
release and cancel if pointer capture is lost. When one of two distinct
sustain endpoints is selected, **Match** appears below the graph and
copies the opposite endpoint's value only when they differ.

Tab/Shift-Tab while the graph is focused cycles nodes; arrow keys
nudge time (10 ms) and value (1% of Vmax-Vmin), without clamping
values. Ctrl-Tab/Ctrl-Shift-Tab navigates to controls outside the graph.

The node-list editor accepts one `timeSeconds, value` pair per line, using
invariant-culture decimals. Node 0 must begin at zero and all later times
must strictly increase. Values are unrestricted finite scalars. Sustain
start/end indices and loop mode are edited separately; **Apply envelope**
validates all fields atomically and creates an audio revision only for an
actual change. Invalid input does not change the live Envelope. This
precise text editor complements the implemented interactive node graph,
which replaces the obsolete ADSR numeric fields and drag caps; see
[node-envelope design](node-envelope-design.md) and
[envelope graph](envelope-graph.md).

Creating a new FM Envelope node leaves its reference unassigned
(`ObjectId.None`), even if Envelopes already exist. Its dropdown starts
blank, with italicized **New...** first, then **(None)** and existing
Envelopes. New... creates a shared **node envelope** in the document's
Envelopes section and assigns it without leaving the FM editor. Clearing a
reference does not delete the shared object. Unassigned FM Envelope nodes
serialize as ID 0 and evaluate to silence; import skips their absent
dependency. Import copies node envelopes and Script Envelopes as their
original types, preserving each node curve or expression.

## Tracker keyboard

The note field supports direct tracker-keyboard entry. A current toolbar Source
(sample, instrument, pattern or sequence) and base octave are editor state; the
toolbar Source is not implicitly written when a note is entered. The physical-
layout convention is the familiar chromatic tracker piano:
`Z S X D C V G B H N J M` spans the lower octave and
`Q 2 W 3 E R 5 T 6 Y 7 U` the next, continuing through `I 9 O 0 P`.
Tracker piano entry is driven from Avalonia's layout-independent physical-key
codes rather than produced text, so changing the operating-system keyboard
layout does not move the musical keys: the physical QWERTY `Z` position still
enters C even if that key currently produces some other character. Entered
pitches are stored as semantic pitch multipliers relative to Heresy's existing
C4 reference convention, so multiplier 1.0 is displayed as `C-4`; exact
equal-tempered semitone multipliers are projected as tracker note names, while
arbitrary multipliers remain visible numerically. `1` enters note cut and
backtick enters note off. The tracker note-entry **skip value** starts at 1.
The toolbar's integer Skip spinner accepts 0–128 rows to advance after a
recognized note/cut/off entry; physical Alt+0 through Alt+9 remain convenient
shortcuts for the corresponding presets and synchronize the spinner. Skip 0
leaves the cursor on the same row; other values move as though Down were
pressed that many times, clamping at the final row and crossing projected
sequence-pattern boundaries (including channel clamps at narrower intermediate
Patterns). Chord entry and completed field-clearing actions use the same Skip.
The selected value is retained when switching standalone patterns.
Without Shift, physical key auto-repeat continues inserting notes in
successive rows using this skip; while Shift is held, piano keys preview
without editing the Pattern. Once a piano key enters preview, its repeats
remain preview-only until the key itself is released, even if Shift is
released earlier. Non-piano Shift shortcuts (selection, Tab navigation,
clipboard actions) retain their previous behavior. Replacing an existing start note changes its pitch
but preserves its playback-speed multiplier and mixdown flag; Enter remains
available for the detailed semantic note dialog.

## Gxx tone portamento and GFF snap

Gxx changes the **base pitch** of an ongoing note, without restarting its
sample, Synth, Instrument, private mixdown, or non-mixdown flattened
Pattern/Sequence source. A note in the same cell supplies the target; if
there is no playing source, the target note can start it instead.
`G01`–`GFE` glide continuously; **`GFF` snaps to the target at the
effect frame**. `G00` recalls the last G byte, including FF's snap mode.
`Lxx` combines that remembered pitch action with its volume slide.
Independent modulation (for example vibrato) continues.

For a flattened nested Pattern/Sequence the controller retunes existing
descendant voices and supplies the updated transposition to future notes,
including Instrument tone selection. Its Pattern-row timing is unchanged.
The editor warns only for effects that are still voice-specific and genuinely
inapplicable to the flattened logical source. For a precise architecture
contract see [effect operators](effect-operators.md).

Tracker users familiar with Impulse Tracker may notice that its GFF means
maximum *finite slide speed*; Heresy deliberately uses FF for an immediate
pitch switch. This comparison is a helpful usage note, not a compatibility
goal.

## Source and volume cells

Source is a separate tracker column and may be omitted. An explicit Source value
updates the remembered source for that mapped physical channel even on a row
without a note. A Start note whose Source field is empty uses that remembered
source; if the channel has never had a source, the note remains valid pattern
data but generates no playback start. Sequencing source memory survives later
pattern invocations in the same sequencing context, while mixdown children
naturally receive independent memory with their independent channel-state map.
During song compilation, Source selections are resolved as each row executes,
not precomputed for the entire pattern. Direct note entry and held-key
preview also support Pattern/Sequence Sources: unlike full playback, a live
event has no originating Pattern timeline to flatten into, so only the
temporary audition voice runs the child as a private recursive invocation.
That choice does not alter the saved note's Mixdown setting; normal
non-Mixdown playback continues to flatten into the shared timeline. A flattened Pattern or Sequence may
therefore change the mapped channel's remembered source before a later parent
row, even when the child merely selects a source without starting a note.
Source changes in skipped rows do not execute; standalone raw-note generator
callers retain their previous eager resolution behavior.
The cursor order is **Note-name → Octave → Source → Volume → Effect
Command → Effect Parameter**. The Octave digit is a separately focusable
subfield: physical top-row or keypad digits change only an existing pitched
Note's octave (preserving its pitch class and other cell properties), then
advance by Skip within that same field; a blank, OFF or CUT Note is not
replaced. Piano keys and the physical QWERTY comma/period positions do nothing
in Octave focus. Clicking any field moves the cursor there, including blank
space.

Plain **Page Up/Page Down** moves the focused Pattern cursor by
`floor(grid viewport height / complete row height)` rows—the same effect
as repeatedly pressing Up or Down that many times. The row height includes
each row's top border, which is shared with the preceding row; the bottom
border is not counted separately. Navigation crosses flattened Sequence
Pattern boundaries, clamps the channel at every intermediate narrower
Pattern, and preserves the focused Note-name/Octave/Source/Volume/effect
subfield. Expanded effect strips collapse as with ordinary vertical arrows.
**Shift+Page Up/Down** extends the rectangular selection as repeated
Shift+Arrow presses would; plain Page leaves the existing selection
unchanged. The editor scrolls only as much as necessary to reveal the
new cursor and does not independently scroll a whole page of content.
**Ctrl+Page Up/Down** still uses the existing Pattern-boundary navigation
shortcut, not this viewport-based cursor movement.
When Source is already focused, clicking it again opens a field-anchored picker
wide enough to show full source names; `Alt+Down` opens the same picker.
Space copies the Source selected in the pattern toolbar into the field, `.`
clears it, and the Source field's right-click menu also provides **Clear**.
In the focused Note-name field, physical QWERTY-period (regardless of OS
keyboard layout) always clears the Note and the entire tracker/native effect
stack. It clears Source only when the Source edit-mask bit is enabled, and
Volume only when the Volume bit is enabled, even if the Note mask bit is off.
Disabled fields retain their values; genuine no-ops do not change the song
revision, and the cursor advances by Skip. Clearing from Source or Volume
focus still clears only that focused field. Effect-field punctuation remains
layout-sensitive and clipboard-region Clear remains a separate whole-cell
operation.

Volume is first-class pattern-cell data, independent of the effect stack. It is
stored as an optional normalized value and presented in the tracker grid as a
decimal `00..64` column (`..` means absent). Two decimal digits set the volume and
advance one row; `.` clears it and advances, so volume values can be painted
down rows even when those rows contain no new notes.

Pattern translation treats that single volume column differently according to
what the row does. For an ordinary `StartPatternNote`, the value is folded
directly into `StartNoteCommand.Volume`, keeping the common note+volume case
atomic and compact. A volume-only row, a note-off row, or a note used only as a
tone-portamento target instead emits `SetNoteVolumeCommand` for the current
note. Note off deliberately keeps the releasing voice attached to its physical
channel, so same-row and subsequent volume-column changes continue to affect
that voice throughout its release phase; more such changes may follow until the
voice ends naturally or is explicitly cut. A note-cut row is different: CUT
ends the current note immediately, so any volume value stored on that same row
is retained as pattern data but ignored during translation and does not alter
the channel's remembered note volume. A direct start volume is applied by
playback only after the source successfully resolves and starts, so a broken
source cannot seed the volume of a later note.

## Ordered effect stacks

Effects are projected as coloured tabs attached to the right edge of each cell.
Multiple effects remain in semantic application order and collapse into an
overlapping stack with a constant five-pixel reveal between tabs; the compact
effect control occupies only the effect column, so it cannot intercept clicks
intended for the note or volume fields and the cell clips the stack so it can
never bleed into a neighbouring channel. Hovering or tapping a stack expands it
over the cell into equal-width tabs side by side. Expanded tabs are right-aligned
when their combined width is smaller than the cell; when they need the full cell
or overflow it, they use the available width and left/right edge controls scroll
the strip on hover or tap. The expanded strip collapses on a click outside it,
or once the pointer moves more than five row heights beyond that pattern row.
Clicking the note, volume, effect-command or effect-parameter region places the
tracker cursor in that field; clicking a visible effect tab selects its logical
field while expanding the stack.

The tracker cursor treats an IT-style effect as two keyboard fields: command and
parameter byte. Typing a command letter replaces/creates a lone tracker effect
while preserving its existing parameter and advances one row; typing two hex
digits replaces the parameter byte and then advances, while `.` writes `00`
and advances immediately. This deliberately supports tracker paint-down entry
such as repeated `G` commands or repeated `15` parameters across rows whose
effect types differ. A collapsed cell containing multiple effects rejects direct
typing until Enter expands it. In the expanded strip, left/right traverse the
individual fields but clamp at the ends; up/down collapse and move vertically,
and Enter collapses in place. Native effects occupy one whole-tab keyboard stop
and ignore direct tracker typing. Enter on a selected native effect, double-
clicking its tab, or choosing **Edit Parameters...** from that tab's context menu
all open the same generic native-effect parameter dialog. The dialog is driven
by a framework-independent semantic edit model rather than effect-specific
Avalonia code: all current native effect shapes project named fields, reconstruct
the same concrete Core effect type, and rely on the Core constructors for value
validation. Clicking **Apply**, or pressing **Ctrl+Enter** anywhere in the
modal (including a focused multiline Filter-assignment field), submits the
same validated edit and closes the dialog on success. Invalid fields leave it
open with diagnostic text so the draft can be corrected and retried; **Cancel**
and Escape retain their normal cancellation behavior. Applying a change replaces
the selected stack member through the same stack-editing command path used by
the tracker UI.

The bottom status area uses a fixed-height content viewport equal to
**2.25 measured lines of its actual themed diagnostic font**, plus its
existing padding. Both diagnostic/status text and effect-specific keyboard
guidance remain **separate, vertically stacked text blocks** inside that
viewport. The vertical scrollbar appears only when the text (including
wrapped multiline messages) exceeds the viewport, stays visible while
overflow exists, and disappears when everything fits. Changing a message
or selecting a longer effect hint never resizes the Pattern grid; the entire
text can be scrolled into view. Font/theme changes update the measured
viewport height rather than relying on a fixed pixel estimate. Guidance is
shown only when the tracker cursor selects an effect field. Empty slots
suggest A–Z or right-click insertion; a single tracker effect describes
command/hex entry; a collapsed multi-effect stack says **Expand stacked
effects with Enter**; an expanded tracker effect describes Enter collapse;
and selecting any native effect says **Configure effect with Enter**.
Moving out of the effect field hides the guidance without erasing the
diagnostic/status line. Bad direct input continues to report the actual
failure in diagnostics (including collapsed stack and native-effect
restrictions), not on the transient guidance line. Mouse hover/tooltips
never replace the existing diagnostic text.

The same dialog also creates native effects. Creation adds a type selector backed
by the native-effect catalog, seeds a valid default instance for the selected
type, then renders exactly the same semantic parameter fields used for editing.
**Ctrl+Enter** is equivalent to the creation modal's **Insert** button too.

The Set Note/Channel Filter Stage Assignments assistant reads the current
multiline assignment script each time a Filter stage is selected. An
unambiguous last recognized comment (`# Filter: name`, `# name: description`
for a Filter, or a uniquely identifying `# stageName: description`) helps
choose the Filter/stage; otherwise the saved authoring hint and first available
stage are used. Existing individually valid assignments populate each numeric
or choice field; absent or malformed values use that Filter stage's defaults.
Opening the assistant never edits the script. **Update this stage's
assignments** changes existing matching parameter rows in place and adds only
missing rows beside them. Existing section annotations, unrelated comments,
other stages and manually authored text stay where they were. A new
`# stageName:` section comment is emitted only for a completely new stage
without assignments or a heading. Repeating the same update is idempotent.
If other script lines remain invalid, the assistant may still update the
unsaved draft and report validation errors; saving still requires the native
effect editor's strict validation.
All eleven current native effect shapes are available. `Alt+N` inserts a native
effect before the selected stack member (or into an empty effect column), while
`Alt+Shift+N` inserts after it. A tab's context menu exposes **Insert Native
Effect Before...** and **Insert Native Effect After...**; an empty effect strip
offers **Insert Native Effect...**. Native insertion is audio-affecting and
selects the inserted native tab immediately, including when it is the only effect
in the cell. The sequence-integrated tracker uses the same mapped cursor path,
so insertion edits the underlying shared pattern rather than sequence-local data.

Effect stacks can be edited without leaving the tracker keyboard flow. `Alt+Insert`
inserts a new IT-style slot before the selected effect; `Alt+Shift+Insert` inserts
after it and selects the new slot. A new slot is persisted as a musically inert
`...` placeholder rather than as a real `xx=00` command, because many tracker
commands use zero as effect memory and therefore are not no-ops. Parameter entry
may precede the command (for example, an empty slot can hold `.15`, then become
`G15` when `G` is entered). `Alt+Delete` removes the selected effect; when only
one effect remains, the expanded stack collapses back to the ordinary single-
effect view. `Alt+Left`/`Alt+Right` reorder the selected effect while expanded,
and `Alt+Home`/`Alt+End` select the first/last stack member. Expanded tabs may
also be dragged to reorder them. A tab's context menu labels tracker insertion
explicitly as **Insert Tracker Slot Before/After**, alongside native insertion
and **Delete**; all operations route through the same framework-independent
stack-editing commands.

Effect copy/paste treats the complete ordered effect list of one pattern cell as
the clipboard unit; note, Source and Volume data are never included or replaced.
`Ctrl+C` (or the platform Meta modifier) while the tracker cursor is in either
effect field copies the stack, and `Ctrl+V` replaces only the destination
cell's effects. The same commands are available as **Copy Effect Stack** and
**Paste Effect Stack** in effect-strip context menus; an empty strip still
offers paste so a copied stack can be placed into a blank cell. The clipboard
payload is plain text with a Heresy pattern-effects/version header followed by
the canonical polymorphic `PatternEffect` JSON representation used by document
persistence. Mixed native/tracker stacks, ordering and inert `...` parameters
therefore round-trip without a parallel clipboard schema. Copying an empty stack
and pasting it clears destination effects while preserving any note, Source or
Volume in that cell. A stack replacement is a no-op when values are identical;
changes involving only inert tracker placeholders advance document revision but
not audio revision. In sequence context, paste resolves through the same mapped
cursor and modifies the underlying shared pattern object.


## Native parametric effect authoring

The Pattern effect sticker's native insertion/editor dialog includes
**Parametric expression**. Select a destination (Global volume,
overall channel volume, spatial X, current note volume or linear
pitch units), specify a wall-time duration in seconds, write a
restricted scalar C# expression using `elapsedSeconds` and
`rowTime`, and choose whether to commit the terminal delta.
Validation happens before replacing the persisted effect; a
compilation failure preserves the existing stack and song
revision. This is independent of tracker effect-letter typing
and compatibility tick timing. See
[effect operators](effect-operators.md) and
[script programmability](script-programmability.md).


### Shared node-envelope workflow and audition validation

The standalone editor and FM graph inspector share the same live node
Envelope object. Clean numeric drafts reflect changes from a second
mounted editor automatically. Unsaved numeric input is never silently
overwritten: if another view updates the Envelope, the stale draft is
blocked from committing until **Reload shared envelope** is clicked.
The two editors retain the same graphical editor control and the
standalone **← Document** navigation.

The automated playback regressions exercise both Forward and PingPong
sustain, Note Off release and eventual silence for direct FM graph
outputs and sample-backed Instrument tone volumes, including irregular
PCM block boundaries. Real desktop audio audition and native pointer
behaviour require a physical smoke pass and remain tracked in
[todo.md](todo.md).


### Graph time-domain refit and FM pitch envelopes

The graph preserves its expanded horizontal domain when its last node
moves left. Extending a node beyond the right edge expands the domain;
the right-aligned **↔** button refits it only when more than one pixel
of empty domain is visible. Refit changes the view, not the song.

FM synth frequency-modulating envelopes have a post-Note-Off voice
lifetime, even if the oscillator output is not amplitude-gated.
The finite release tail is heard before the FM voice retires.

## Global output oscilloscope

**View → Output oscilloscope** toggles a live dock around the current
main-window content without replacing the active document editor.
The scope starts hidden. It can dock on **Top, Bottom, Left, or Right**:
drag the scope title bar toward the desired edge and release. A
6-pixel `GridSplitter` between the scope and document allows resizing
its thickness; horizontal and vertical dock thicknesses are remembered
within the window session. The small × control closes it and
synchronizes the View-menu checkmark.

The display shows the newest **final SDL-queued output frames** with
one frame per horizontal physical pixel, refreshing at approximately
30 Hz. The display does not synthesize or aggregate waveform peaks.
An empty capture or stopped playback is blank (not invented silence);
actual underrun-inserted PCM zeros appear as a flat center line.
Mono has one blue trace. Stereo and surround default to overlaid
front L (blue) and front R (orange); the header selector can switch
to any individual speaker, including Center, LFE and rear/side
surround channels. The view is the actual interleaved speaker output
after all final processing, independent of the currently selected
instrument or song editor. See [audio output](audio-output.md).
