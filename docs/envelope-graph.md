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

The standalone Envelope editor and the FM Envelope-node inspector both
reference one live Envelope object. When a different mounted editor changes
that curve, clean numeric fields refresh automatically. If the user has
uncommitted numeric changes, the editor preserves them and warns that the
shared object changed: applying the stale draft is refused. **Reload shared
envelope** explicitly discards that draft and picks up the new curve.
The graphical view stays subscribed to document changes while mounted.

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
