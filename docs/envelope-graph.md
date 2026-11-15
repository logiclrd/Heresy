# Interactive node envelope graph

The standalone Envelope editor and the FM synth Envelope-node inspector
share `NodeEnvelopeGraphControl`, backed by testable
`NodeEnvelopeGraphGeometry`. The graph renders a piecewise-linear
polyline, a faint vertical guide at each node, the sustain interval
highlighted using ARGB `#40808080`, and a small node dot.
Only one node can be selected, and a click selects the nearest dot
within **4 pixels**; clicking elsewhere clears selection. A selected
node has small brackets left and right, each **2 pixels taller** than
the node diameter with a gap from its edges.

Click and drag a node to change its time and value; its time cannot
cross a neighbour. Node zero's time remains zero, although its value
is editable. Double-clicking a line inserts a node at the nearest
point on that segment (if there is a distinct representable tick
between its neighbours), and double-clicking a node removes it.
Removing the first node re-bases the remaining node times to zero;
the last node cannot be removed. Sustain index references update
when inserting or deleting nodes.

The graph starts fitted to the final node and maintains a persistent
time-domain extent: shortening the envelope does not automatically zoom
in, while stretching past the right edge expands the domain. When the
last node leaves more than one pixel of unused space on the right, the
right-aligned **↔** button refits the view; it never modifies the
envelope or increments the audio revision.

Changes are committed once on pointer release, with live visual
feedback and cancellation on capture loss. Drag rendering uses the
current preview nodes, not cached node locations. Vertical scale is
fixed by persisted Vmin/Vmax, with a 5% margin above and below.
Two **thin, 1-pixel green horizontal guide lines** mark the configured
**Vmin** and **Vmax** positions using the *same value-to-pixel transform*
as the envelope's nodes. They span the graph plotting area, draw above
sustain shading but behind the vertical guides, node curve and dots, and
remain within the 5% top/bottom margins even when the node-data range
lies entirely outside the configured bounds. The former thick gray
**bottom baseline is removed** and replaced by a thin gray horizontal
line at the **numeric zero** value *only when visible*. Green bound lines
are drawn over this gray line, so a Vmin/Vmax value of zero takes
precedence. The guides recompute on bound edits, node changes and
viewport resizing; changing just the display bounds or viewport does not
modify the audio revision. With the fixed percentage margin, the guides
retain constant *relative* heights when only the numerical bounds
change, while the mapped node values and numeric zero position change
accordingly.

Vmin/Vmax each have a label centered vertically on their corresponding
green line **to the left of the plot**. Both labels use the same
fixed-point decimal precision: at least three fractional places
(default `0.000` / `1.000`), adding decimals when necessary to represent
the smaller nonzero magnitude to three significant figures, so
`0.0045` / `0.007` appears as `0.00450` / `0.00700`. Neither uses
scientific notation. The plot reserves the measured label width.

Thin **gray vertical time grid lines** subdivide the visible time domain
at the largest integral power of ten not exceeding the last node time
in seconds divided by `sqrt(10)`. For a last time `0.29`, the spacing is
`0.01`; at `0.32`, `0.1`; at `3.1`, `0.1`; and at `3.2`, `1`.
A fixed-point label appears beneath every line, with decimal places
matching the grid interval (e.g. `0.30` at 0.01 spacing versus `0.3`
at 0.1 spacing). Labels use short staggered lanes when grid density is
high. The separate vertical guides attached to each **node** use the
main curve's light-blue color at **40% opacity**, with existing thicker
sustain-endpoint guides preserved.

When the mouse is inside a node dot's 4-pixel-radius draggable region,
a compact overlay displays its **actual stored time/value** beneath the
pointer. The overlay tracks pointer movement and disappears when the
pointer leaves the hit area or the graph. If several nodes overlap the
same drag region, one overlay per matched node appears, stacked in node
order with a **2-pixel gap**. These purely visual overlays do not modify
hit-testing precedence, pointer capture, or audio revision.
None mode omits sustain shading and traverses the envelope only once.
Tab/Shift-Tab cycles node selection, arrow keys nudge time and value,
and Ctrl-Tab/Ctrl-Shift-Tab exits the graph. The document editor
validates the replacement curve atomically, marks audio-affecting
revisions, and updates the auxiliary numeric node-list editor.
The sustain region is filled with ARGB `#40808080` (25% opacity of
50% gray). Its **start and end vertical guides are 3 pixels thick**,
three times the ordinary guide width; a coincident start/end therefore
remains clearly visible even though the filled band has zero width.
Guide hit targets extend 2 pixels on each side, for a total 5-pixel
draggable strip. Hovering either endpoint shows `SizeWestEast`;
hovering another guide shows `Ibeam`. The cursor remains fixed
throughout each drag.

Dragging a start/end guide moves **only that boundary**, even if it
crosses the other one; the resulting start/end indices reorder.
When both endpoints coincide, dragging expands the interval in
either direction. Dragging from **any other guide**, including one
inside the existing sustain interval, replaces the entire region
between that guide and the destination, regardless of direction. Self-drag leaves the old region untouched;
double-clicking any guide *away from the node dot* explicitly creates
a single-node sustain. The node dot takes precedence over guide hits,
so its own double-click still deletes that node. Guide drags preview
the highlighted interval but commit only on pointer release; pointer
capture loss discards the preview.

The untouched endpoint retains its original index when a boundary is
dragged outward or across it; a non-endpoint drag defines a new
region from scratch.

With a sustain endpoint selected, a **Match** button appears directly
beneath the graph if and only if sustain start and end differ. It is
disabled when their values agree. Otherwise it copies the opposite
endpoint's value into the selected node without changing either time
or sustain index, committing a single audio-affecting document revision.
Numeric node and sustain fields remain synchronized. Full desktop
physical validation remains tracked in
[node envelope design](node-envelope-design.md). Script Envelopes
retain their independent script editor.

## Shared-editor synchronization and desktop verification

The standalone Envelope editor, FM Envelope-node inspector and
modeless Instrument Volume window can share one live Envelope object.
Each accepted change raises an **object-level Changed event** after the
complete validated state is installed. Mounted graphical and numeric views
refresh immediately on that event, independent of unrelated song changes.
**Last committed edit wins:** if two views both have pending raw node text
and one accepts its text with **Set nodes**, the other view's unfinished
text is replaced with the accepted shared state. No conflict dialog or
Reload button is needed in this single-user workflow.

**Set nodes** sits immediately below the raw node-data textbox and
modifies *only* the nodes, using the current accepted sustain indices,
loop mode and display bounds. Raw node text is otherwise staged until
Set nodes is clicked, even when the editor loses focus or closes. The
separate Sustain start/end, Loop mode, Vmin and Vmax controls are organized
in a four-column label/input Grid (maximum width **800 px**) and each
commits independently on focus loss. Pending valid parameters are also
committed before Save, switching views, New/Open or exiting the editor;
invalid fields remain uncommitted with a validation message and can
block a guarded document transition. Parameter edits never reparse or
silently apply the raw node-data draft. Display limits change the
document revision but not the audio revision; curve/sustain/loop edits
retain their ordinary audio revision and shared playback publication.
An accepted change in another view replaces all older uncommitted
parameter or node text there, rather than preserving a stale draft.

Headless CI now simulates mouse drags and navigation, and verifies
synchronization across mounted standalone/FM editors. Playback tests
render FM graph envelopes and sample-backed Instrument volume envelopes
through sustain, release and retirement for both loop modes, comparing
continuous and chunked PCM. These tests do not confirm that real mouse,
touchpad, Wayland/X11/Windows focus behaviour, or the actual audio
device matches the headless simulator.

Manual desktop smoke checklist:

1. Open one Envelope via the Envelopes pane and through an FM Envelope
   node. Add/move/remove dots; drag guides from outside the sustain
   region in both directions; drag sustain endpoints to one another;
   double-click a guide *away from the dot* to make a single-node
   sustain. Confirm the node double-click still deletes.
2. Select either sustain endpoint and test **Match** enabled and
   disabled states. Test a pointer release outside the graph, loss
   of capture, numeric editing, and changing the same Envelope from
   another view while one numeric draft is unsaved.
3. Switch between Forward and PingPong and audition a sustained
   Instrument note, then Note Off. The envelope must continue from
   its current point and finish naturally. Test the FM editor Test
   area likewise, including a graph with the Envelope multiplying
   an oscillator, and verify no premature cut or stuck voice.
4. Save and reload the song, verify node values and sustain mode,
   switch views, and repeat. Capture any Wayland/X11-specific
   pointer or focus discrepancy as a separate reproducible issue.
