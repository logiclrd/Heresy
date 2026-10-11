# Node envelopes: data model, playback, and graphical editing

## Intent and migration policy

The new **Node Envelope** supersedes the four-scalar ADSR envelope.
There are no existing user songs requiring a compatibility loader, migration
schema, or dual-format maintenance. The cutover will delete the ADSR
model/editor/renderer once their call sites have been moved. Scripted
Envelopes remain a distinct supported expression-based envelope type.

The production authoring and persistence cutover is implemented.
Ordinary envelopes use the node model, Script Envelopes remain separate,
and the former ADSR model, renderer, editor, and JSON discriminator were
removed without a legacy compatibility loader. The standalone and FM inspectors now share an interactive node graph
with an auxiliary editable node list for precision.

## Display range and no-sustain playback

Every node Envelope stores **Vmin** and **Vmax** (default 0 and 1).
These finite, ordered values configure the editor's vertical axis
only: 5% of (Vmax - Vmin) is displayed beyond both ends. The values
do not normalize, clamp, or change the sound, and nodes outside these
bounds remain valid. Display edits produce document revisions without
audio revisions. JSON, snapshots and FM imports preserve the bounds.

Loop mode **None** simply traverses the nodes once, ignoring sustain
indices and continuing past them whether or not Note Off occurs.
The output becomes silent after the final node.

## Data contract (milestone 1)

A `NodeEnvelopeDefinition` owns at least one immutable value node
`EnvelopeNode(TimeSpan Time, double Value)`. Nodes are ordered with strictly
increasing nonnegative times and **node 0 is at t=0**. Every value must be
finite, but may be negative or exceed 1: the envelope may target volume,
pitch, panning, filters, or FM signals.

`SustainStartIndex` and `SustainEndIndex` are integer indices into the
nodes, inclusive, satisfying
`0 <= SustainStartIndex <= SustainEndIndex < Nodes.Count`.
`EnvelopeSustainLoopMode` is Forward or PingPong. One-node envelopes are
legal, with both indices zero. A single sustain node forms a constant hold
irrespective of loop mode. Nodes, sustain indices and loop mode are replaced
**atomically** after full validation; the editor marks an audio/document
revision only when an actual value changes.

The persisted discriminator is `nodeEnvelope` with
`nodes`, `sustainStartIndex`, `sustainEndIndex`, and
`sustainLoopMode`. Frozen song snapshots copy the full shape. No
`attack`, `decay`, `sustainLevel`, or `release` scalar fields are
part of the new shape. The initial editable shape begins at 0, rises to 1,
falls to 0.7 at the single sustain node, then ends at 0.

## Playback semantics (next milestone)

Before Note Off, envelope time normally advances forward and the output
is obtained by linear interpolation between adjacent nodes.

- **Forward sustain**: reaching the sustain-end node while held wraps
  envelope time immediately to the sustain-start node; the resulting
  value discontinuity is intentional and is not automatically smoothed.
- **PingPong sustain**: reaching sustain end while held reverses motion
  toward sustain start; reaching start reverses it toward end. These are
  continuous reversals, not jumps.
- **Single-node sustain**: reaching that node while held pins the value;
  no time advances past it until Note Off.
- **Note Off**: does not jump to sustain end or reset envelope position.
  Instead, the current traversal continues naturally. Any backward
  ping-pong traversal continues to the sustain start and turns forward
  there; the next forward pass through sustain end does not wrap or reflect.
  Note Off before entering sustain likewise disables future looping.
  A pinned single-node sustain resumes moving forward from its node.
  All subsequent nodes are traversed in their original temporal order.

Real-time rendering is driven by the existing **active-envelope frame**
clock, including pause/resume and historical value queries. The immutable
curve must support exact deterministic evaluation with a note-off frame
and must define a sound end bound when the target's volume semantics
permit one. An envelope's last node describes its last defined value,
which must not be forcibly normalized/clamped. Separate target and voice
retirement logic must handle tails correctly.

## Graphical editing contract (interactive graph and sustain gestures implemented)

The replacement graph draws small nodes and straight interpolating
segments, faint vertical guides through every node, and a sustain region
plus its corresponding lines highlighted with **#04808080**.
At most one node can be selected. A normal click selects the closest
node if it lies within **4 pixels** of the pointer and otherwise clears
selection. The selected node is marked with tiny brackets to its
left and right, each **2 pixels taller than the dot**, separated from
the dot by the smallest visible gap.

Double-clicking a segment projects onto that segment and inserts a node
at the nearest valid point along it, retaining strict time ordering.
Double-clicking a node deletes it and normalizes sustain indices while
keeping at least one valid node. Node hit testing takes precedence over
segment insertion. Dragging a node edits its time and value against a fixed viewport,
clamping time to keep it between its neighbours. The first node remains
at time zero but can change value. Deleting the first node shifts
the remaining timeline so its next node becomes the zero-time origin.
Deleting the last remaining node is disallowed. Sustain indices follow
the same surviving nodes across insertions/deletions.

Dragging an endpoint guide inward resizes the current region,
without crossing the opposite endpoint. Dragging an endpoint guide
outward instead selects the interval from that guide to the destination,
e.g. dragging line 2 to line 4 selects [2,4].
When start and end coincide, either boundary may expand to a new
node according to drag direction.
Dragging from a non-highlighted node's guide to a different node's guide
replaces the entire sustain interval; direction does not matter.
Dragging from a guide back to itself leaves the region alone;
**double-clicking a guide** explicitly sets the sustain start and end
to that same node. Graph preview remains transient during drag, and
pointer release commits once; lost capture cancels. Double-clicking a
node dot takes precedence over its guide; guide double-clicks must
occur away from the dot.

When either sustain endpoint node is selected and the endpoints differ,
show a **Match** button below the graph. It is disabled if both
endpoint scalar values already agree. Otherwise clicking it sets
only the selected endpoint's value to the opposite endpoint's value
and commits through the normal document revision path.

Tab/Shift-Tab cycles through nodes while the graph is focused;
arrow keys nudge time by 10 ms and value by 1% of Vmax-Vmin.
Time remains ordered, node zero stays at time zero, and values do
not clamp. Ctrl-Tab/Ctrl-Shift-Tab navigates out of the graph.

The sustain fill now uses ARGB **#40808080**, and the graph background
responds to light/dark themes. A fixed viewport transform and live
node coordinates ensure drag preview updates without vertical rescaling.

The standalone envelope editor and FM Envelope-node inspector must
share this graphical control, replacing the former ADSR numeric editor
rather than displaying obsolete Attack/Decay/Sustain/Release fields.
Desktop checks should exercise selection and hit tolerances, duplicate
time constraints, backward sustain drags, one-node matching, and
out-of-range node values.

## FM editor audition key-up (distinct from Pattern playback)

FM synth notes **already use proper Note Off during Pattern playback**,
and the FM editor's Test area already emits `NoteOffCommand` on key-up.
The premature cutoff was caused by the live virtual-voice safety policy:
a sound whose finite end could not be determined was cut immediately.
FM envelopes are compiled **inside** `FmSynthSound`, rather than being
attached as the voice's ordinary volume envelope, so that path had no
end bound even when the FM graph would become silent after release.

`FmSynthSound.GetEndFrameExclusive` now recognizes an output graph whose
embedded Envelopes promise finite silence. A directly output Envelope ends
at its own deadline; a Multiply becomes silent when any factor ends; an
Add, Minimum or Maximum requires all inputs to end. An Envelope affecting
only oscillator frequency does not justify an output-silence guarantee.
Ungated/unbounded FM output retains the existing safety cut, preventing
indefinitely sounding preview voices. Live virtual Note Off now preserves
finite FM release tails, as covered by `FmSynthPreviewReleaseTests`.
FM preview retains physical key-up Note Off and remains intentionally
different from the Sample editor's persistent preview-note behaviour.
