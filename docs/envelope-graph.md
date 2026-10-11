# Node envelope editor transition

The former four-scalar ADSR graph, geometric handles and editor were
removed with the production node-envelope cutover. This documentation is
retained at the original path to avoid a dead link from earlier project
notes. The active design is [node envelope](node-envelope-design.md).

In the current implementation, the standalone Envelope editor and FM
Envelope node inspector share a temporary text-based node-list editor,
including sustain index and loop mode controls. The next UI milestones
replace it with an interactive polyline graph, node hit testing and
insertion/deletion, sustain guide dragging, and Match as specified in the
design. Script Envelopes continue using their own scripting editor.
