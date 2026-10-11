# Interactive node envelope graph

The standalone Envelope editor and the FM synth Envelope-node inspector
share `NodeEnvelopeGraphControl`, backed by testable
`NodeEnvelopeGraphGeometry`. The graph renders a piecewise-linear
polyline, a faint vertical guide at each node, the sustain interval
highlighted using ARGB `#04808080`, and a small node dot.
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

Changes are committed once on pointer release, with live visual
feedback and cancellation on capture loss. The document editor
validates the replacement curve atomically, marks audio-affecting
revisions, and updates the auxiliary numeric node-list editor.
Sustain boundaries have draggable vertical guides: dragging a start/end
guide snaps to another node, clamping before the opposite endpoint.
When both endpoints coincide, a drag can expand the interval in either
direction. Dragging from a guide **outside the existing sustain region**
replaces the region between that guide and the destination guide,
regardless of drag direction. Self-drag leaves the old region untouched;
double-clicking any guide *away from the node dot* explicitly creates
a single-node sustain. The node dot takes precedence over guide hits,
so its own double-click still deletes that node. Guide drags preview
the highlighted interval but commit only on pointer release; pointer
capture loss discards the preview.

With a sustain endpoint selected, a **Match** button appears directly
beneath the graph if and only if sustain start and end differ. It is
disabled when their values agree. Otherwise it copies the opposite
endpoint's value into the selected node without changing either time
or sustain index, committing a single audio-affecting document revision.
Numeric node and sustain fields remain synchronized. Full desktop
physical validation remains tracked in
[node envelope design](node-envelope-design.md). Script Envelopes
retain their independent script editor.
