# Node envelopes: data model, playback, and graphical editing

## Intent and migration policy

The new **Node Envelope** supersedes the four-scalar ADSR envelope.
There are no existing user songs requiring a compatibility loader, migration
schema, or dual-format maintenance. The cutover will delete the ADSR
model/editor/renderer once their call sites have been moved. Scripted
Envelopes remain a distinct supported expression-based envelope type.

This change is delivered in milestones so each commit builds and tests:
the initial milestone introduces the validated node definition and its JSON
persistence alongside the old ADSR implementation *temporarily*, not as
a promised legacy format. In a later milestone the default creation and FM
inspector switch to node envelopes and ADSR support is removed.

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

## Graphical editing contract (later milestones)

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
segment insertion. Node moves may not cross neighbours in time.

Dragging either highlighted sustain boundary guide relocates that
boundary to another node, **never past the opposite boundary**.
Dragging from a non-highlighted node's guide to a different node's guide
replaces the entire sustain interval; direction does not matter.
Dragging from a guide back to itself leaves the region alone;
**double-clicking a guide** explicitly sets the sustain start and end
to that same node. Graph preview remains transient during drag, and
pointer release commits once; lost capture cancels.

When either sustain endpoint node is selected and the endpoints differ,
show a **Match** button below the graph. It is disabled if both
endpoint scalar values already agree. Otherwise clicking it sets
only the selected endpoint's value to the opposite endpoint's value
and commits through the normal document revision path.

The standalone envelope editor and FM Envelope-node inspector must
share this graphical control, replacing the former ADSR numeric editor
rather than displaying obsolete Attack/Decay/Sustain/Release fields.
Desktop checks should exercise selection and hit tolerances, duplicate
time constraints, backward sustain drags, one-node matching, and
out-of-range node values.

## FM Note Off integration

A note using an FM synthesis graph with an assigned Envelope node
must receive the same continuing **Note Off** semantics as an
enveloped instrument voice, not a premature Cut. It must have enough
lifecycle support to play out the node envelope after release.
FM graphs without an envelope continue their current non-envelope
behaviour. Test both direct FM notes and FM sources selected indirectly,
including release during backward ping-pong traversal.
