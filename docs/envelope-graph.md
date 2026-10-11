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
Sustain boundary dragging, guide-to-guide region selection, and
Match editing are planned for the next milestone, documented in
[node envelope design](node-envelope-design.md). Script Envelopes
retain their independent script editor.
