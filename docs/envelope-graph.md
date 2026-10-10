# Interactive ADSR envelope graph

## Control and data ownership

`Heresy.UserInterface.Views.AdsrEnvelopeGraphControl` is a reusable
Avalonia control with a `DocumentWorkspace` and a **live**
`AdsrEnvelopeDefinition` reference. It validates that the envelope
belongs to the workspace and commits changes through the existing
`EnvelopeDocumentEditor.UpdateAdsrEnvelope`; no new envelope format,
duplicate serialized values or renderer is introduced.

The standalone `EnvelopeEditorControl` now hosts it above the
existing numeric Attack/Decay/Sustain/Release fields. Numeric **Apply**
updates the graph; completed graph edits update all four numeric fields
and invoke the usual document-changed/status callback. The graph
also subscribes to the document's Changed event while attached and
unsubscribes when detached. The same control is **also hosted** in the FM synth editor's
Envelope-node inspector. It edits the exact live envelope object,
with the same document/audio revision path; switching the FM node's
selected Envelope rebuilds that panel against the new object.

## FM editor: creating and assigning envelopes

The FM editor permits Envelope nodes without preexisting
Envelope objects. Every new node initially has an unassigned
`ObjectId.None` value and a blank selection. The selector starts
with italicized **New...**, followed by **(None)** (the clearing
action) and then existing Envelope names/IDs. Choosing New... opens
a name prompt inside the FM editor, creates an Envelope in the
first-class Envelopes section, assigns it to the FM node and embeds
this same graphical control without opening another editor view.
Canceling the name prompt leaves both the graph and assignment
unchanged. Assigning another existing Envelope switches which
shared definition is graphically edited; clearing an assignment
does not delete it.

Unassigned `FmEnvelopeNode` references use `ObjectId.None` and
produce zero FM signal, as missing unresolved curves already did.
ID 0 roundtrips through the current version-one song JSON format
and is ignored when collecting dependencies for FM import.
Imports leave such nodes unassigned, whereas concrete Envelope
references are still imported/remapped normally. Nonzero
assignments are validated as live Envelopes.

Regression coverage: `FmUnassignedEnvelopeEditorTests`,
`FmSynthGraphTests`, `FmSynthSoundTests`, and existing
FM import/persistence tests. A desktop smoke check of New... menu
formatting, popup focus, cancellation, and embedded graph pointer
interaction is still desirable.

## Graph shape, coordinates, and zero-duration handles

The plot shows the ADSR path: volume 0 → 1 during Attack, then 1 →
Sustain during Decay, Sustain held horizontally, and Sustain → 0
during Release. The horizontal Sustain segment is a **display-only
hold interval** (at least 0.5 seconds visually, sized relative to
the total timed duration) because the musical Note Off moment is not
part of an ADSR definition; adjusting it does not write a duration.

Each duration endpoint has a draggable vertical guide and a
distinguishable top cap in one of three lanes:

- Attack endpoint at `Attack`, top lane.
- Decay endpoint at `Attack + Decay`, middle lane.
- Release endpoint at `Attack + Decay + visual hold + Release`,
  bottom lane.

When any duration is zero its endpoint coincides with its start.
Clicking that endpoint or its cap drags the **ending** boundary, which
allows a zero duration to become positive; separate cap lanes allow
targeting Attack vs Decay even if **both** are zero. Outside the
specific cap lanes, the later overlapping endpoint wins.
The graph scales the horizontal axis to fit with room for dragging
the Release endpoint. On pointer press, pixels-per-second and visual
hold interval are frozen for that gesture, avoiding feedback as the
viewport would otherwise rescale during a drag. It refits on release.

## Sustain and out-of-range values

Reference Y=1 at the top is labelled **Note Volume**; Y=0 at the
bottom is labelled **0**. The right-hand **Sustain** annotation
tracks the horizontal Sustain segment and reports the actual scalar.

The Core envelope permits finite Sustain levels below 0 and above 1,
so these values are **not** silently normalized. They are visually
clipped to the 0 or 1 edge and the annotation includes **(clipped)**.
Dragging the captured horizontal segment outside the graph boundaries
can produce finite negative or above-unity Sustain values.

The graph renderer presents an explanatory visual only; actual
per-frame voice envelope semantics continue to be implemented by
`AdsrEnvelopeCurve` in Heresy.Render, including release from the
actual held value if Note Off occurs before Attack or Decay complete.

## Commit, validation, and regression tests

While dragging, only the graphic and live Sustain label preview.
On pointer release, a changed value is converted to TimeSpan/finite
scalar, validated, and committed in one document operation, advancing
audio and document revisions. A pointer-capture cancellation reverts
to the original display without changing the song. The previous
renderer uses frozen song snapshots; graph edits do not mutate
already-started audio.

The framework-independent `AdsrGraphLayout` and
`AdsrGraphValues.MoveHandle` are exercised by
`AdsrGraphGeometryTests` for centering, zero-overlap cap selection,
duration clamps, Sustain hits and scalar preservation. Existing
`EnvelopeDocumentEditorTests` cover live object identity, revision
changes, no-op updates, invalid durations, and finite but unbounded
Sustain scalar values. Native GUI pointer/label layout can be
additionally smoke-tested on desktop.
