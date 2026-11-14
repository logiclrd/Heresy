# Definite and Indefinite Note Off — recursive sound lifetime

Status: implemented on draft PR #12; automated PCM/regression acceptance and
specific real-device listening observations are recorded below. This is a
Heresy sound-lifecycle contract, not an Impulse Tracker compatibility rule.
Listening does not replace renderer-state assertions or cover every possible
source/envelope combination.

## Vocabulary

- **Note Cut**: unconditional termination of the selected note and its descendants,
  retaining Heresy's existing anti-click treatment. It is not a release event.
- **Definite Note Off**: the event delivered by pattern/transport/preview controls
  at the top-level note. If the active sound subtree has no sustain-release
  lifecycle, it behaves as Note Cut.
- **Indefinite Note Off**: a release request forwarded by a releasing parent.
  It breaks participating sustain loops but does not cut sources which have no
  sustain-release lifecycle. Their natural EOF or other independently known
  finite end still applies.
- **Sustain-release lifecycle**: at least one **active, enabled sustain loop**
  in any applicable envelope in the *selected* and *reachable* sound subtree.
  This applies to all envelope targets: Volume, Pitch, Panning, Filter,
  oscillator modulation, and nested sources, including nested Synths and
  Instruments. The mere existence of a non-looped envelope does not qualify.
  A configured loop qualifies even if Note Off arrives before its first
  traversal. Disconnected Synth nodes, inactive tone choices and dormant
  sources do not qualify.

## Event semantics

| Event | Subtree has sustain-release lifecycle | No sustain-release lifecycle |
| --- | --- | --- |
| Definite Note Off | Break sustain loops; propagate Indefinite to children | Cut |
| Indefinite Note Off | Break sustain loops; propagate Indefinite to children | Ignore this Off as a termination request |
| Note Cut | Cut whole subtree | Cut whole subtree |

A transparent parent that has no local sustain loop **but whose selected child
does** passes the release request down rather than losing it. When the first
releasing ancestor begins processing Note Off, children receive Indefinite.
The release flag and original timing are one-shot; repeated Note Off must not
reset release origins. A cut after Note Off must remain possible.

## Lifetime/retirement

- A child without its own sustain loop continues supplying sound after an
  *Indefinite* Note Off while the enclosing releasing parent needs it. It is
  *not* guaranteed infinite life: finite Samples still end at natural EOF,
  time-limited filters exhaust their authored tail, etc.
- Several sustained envelopes can release concurrently, even when controlling
  unrelated targets. An envelope terminating on a **nonzero** Volume, or one
  ending Pitch/Filter control, must not automatically terminate other active
  releases. All applicable release lifecycles need to complete before ordinary
  retirement, unless another independent end proves silence.
- If the main final-output Volume envelope **has finished** and is permanently
  zero, the note can retire earlier. A momentary zero crossing is not enough.
  This optimization is valid only when downstream processing cannot have an
  audible tail; otherwise preserve the tail. It is not a generic rule for a
  mid-graph Volume controller.
- The note must not retire while any audible release or bounded crossfade
  remains, and must not live forever only because a released envelope finishes
  at a nonzero value.
- A Synth graph's output topology (Add vs Multiply, Filter tails, nested
  Sources and Multi children) must be respected when proving final silence.
  Avoid accidentally treating a child envelope as an independent audible
  signal or erroneously stopping an audio branch merely because an unrelated
  control envelope ended.

## Implementation and regression criteria

- The top-level playback voice accepts Definite Note Off. Recursive child
  state can carry Indefinite without synthesizing a Note Cut or rewriting
  immutable prepared sound definitions.
- The release-capability calculation is based on the frozen bound invocation
  and its active/selected subtree, not all available Instrument tones.
- The prior one-off `FmSynthSound.ExtendExternalVolumeRelease` and
  `SampleSoundState.ReleaseIsControlledByVolumeEnvelope` compensations have
  been removed. `SoundState.NoteOffDisposition` and recursive Synth lifetime
  accounting now preserve the same audible releases without Volume-only rules.
- Regressions: bare Sample/bare oscillator Definite cut; Indefinite continued
  generation/EOF; sustained Volume/Pitch/Filter; no-loop envelopes; nested
  Instrument→Synth; Synth→Instrument; multiple concurrent releases; permanent
  final Volume zero vs nonzero; downstream Filter tail; explicit Cut and
  repeated Note Off; Multi/Operator graph semantics.
- Keep envelope clocks, selected tone identities and live-edited Sample
  playheads intact; do not allocate or rebuild note states in the PCM callback.

## Implementation and validation — slice 48

- Frozen `IEnvelopeCurve.HasSustainLoop` identifies node sustain loops. The
  existing scripted envelope model has no authored node/loop indices: when a
  scripted curve declares a finite release duration, it is treated as an
  implicit sustain/release lifecycle for backward-compatible release behavior.
  A script without a declared release duration does not claim one.
- A completed node envelope retains the value of its **last node**; the
  finite release-completion bound and the permanent-silence proof are separate
  concepts. In particular a Volume endpoint of 0.1 is not a cut.
- `PlaybackVoice` combines enabled local releases; `FmSynthSound` recursively
  checks active, frozen Sources and Multi constituents and forwards Indefinite
  Off through nested sound invocations. The earliest natural Sample EOF and
  authored Filter tail remain independent limits. Existing PCM anti-click,
  Note Cut and note-event ordering continue to apply.
- A post-tone-Filter Source Volume which finishes at permanent zero proves
  that Source silent at its completion, except for any currently outgoing
  bounded Volume-edit crossfade. A subsequent graph Filter still receives
  its authored tail budget. Multiple independent releases otherwise continue
  until their last completion.
- Tests were introduced red-first for the non-sustained Pitch-only Definite
  Off case. Regression coverage now includes Instrument→Synth release,
  Instrument Pitch-only release protecting an unsustained Synth, Source-bound
  Instrument Volume0 vs Volume10% with longer Pitch release, multiple Synth
  release curves, synthetic script-like release curves, nested Sample EOF,
  Operator/Multi paths, live Volume-edited gain overlap and NNA lifetimes.
  The earlier code's audio-local envelope dependency tree has been retired.
- These tests are automated render/transport acceptance, not a substitute for
  physical listening of latched notes or sustained live-device performance.
  PR #12 remains draft, and the full nonstructural-live-property TODO is open.


## Physical-device listening — recursive Note Off (2026-11-11)

The user tested Note Off and Note Cut in the real Synth Test and Pattern
interfaces. These are **observed audible/UI results**, not assertions about
internal renderer retirement or a complete all-topologies acceptance matrix.

| Case | Construction and observation | Manual verdict |
| --- | --- | --- |
| Parent sustaining, child non-sustaining | Instrument's sustain-loop Volume Envelope releases while an Instrument-selected Synth has no lifecycle of its own. Verified with child Oscillator, Sample Source, and two Sample Sources added by Operator. | **Pass** — Indefinite Off preserves child excitation during parent's release. |
| Sustain exists only in child | Instrument has no envelope; selects Synth containing a looping Sample whose Source pitch multiplier is controlled by a sustain-loop Envelope (audible record-scratch modulation). A separate Synth with an Oscillator and sustain-loop Envelope multiplied into Volume was also tested. | **Pass** — child lifecycle is recognized through transparent Instrument parent. |
| No sustaining lifecycle | Definite Note Off on plain Oscillator Synth, Sample Synth and Instrument selecting non-releasing Synth. | **Pass** — immediate cut with normal handling. |
| Pitch-only sustain | Sustain-loop Envelope on pitch multiplier of a Sample-backed Source and separately an Oscillator, with no Volume sustain. | **Pass** — pitch modulation's release is audible; release is not limited to Volume. |
| Two independent releases | Sample Source pitch multiplier driven by a spiky sustain-loop Envelope with roughly 3-second release following a nearly 1-second loop; downstream Multiply Operator scales Volume with a different Envelope, roughly 1-second release that rises to nearly full Volume and ends nonzero. Tested Synth preview and Pattern Note Off. | **Pass** — the shorter Volume release does not truncate the longer pitch release. |
| Note Cut during release | Explicit CUT added after OFF in a Pattern while the note was still releasing. | **Pass** — remaining release cuts immediately. Repeated OFF origin was *not* manually checked. |
| Off before first sustain traversal | OFF one Pattern row after Note On. | **Pass** — envelope plays from start through release, traversing the sustain-loop portion once without looping. |
| Sample natural EOF under release | `layer3-continuous-mono.mp3` from tests (just under 0.5 s) passed through Source-only Synth into Instrument with envelope reaching sustain at 0.1 s and having 1.3 s release; Pattern OFF one row after note start. | **Pass** — audio ends at natural Sample EOF, not at the later envelope release deadline. |
| Downstream Filter tail | Instrument with the same short Sample and Volume Envelope (sustain at 0.03 s, reaching zero at 0.1 s) used as a Source in a Synth feeding a Comb Filter at maximum feedback with 2 s tail. Tested held/released Synth preview and Pattern OFF. | **Pass** — Comb tail remains audible after Volume reaches zero and after Note Off. |
| Independent release cycles in two branches | Two Oscillators, each with a different sustaining Pitch Envelope: one rises over roughly 1.7 s, the other drops to zero rapidly. Tested held, normal release and very early release. | **Pass** — neither branch prematurely truncates the other; an early OFF causes both envelopes to play through once without looping. |
| Permanent-zero final Volume retirement | Proposed comparing shorter Volume release ending 0% with one ending nonzero while another envelope continues. | **Not manually assessable** — silence cannot reveal whether internal voice processing has stopped. Retain automated PCM/lifetime assertions; no listening signoff claimed. |

**Acceptance scope:** The user has manually verified the principal release
contract across parent-only and child-only sustain, pitch-only sustain,
non-sustaining Definite cuts, concurrent independent releases, explicit Cut,
pre-sustain Off, Sample EOF, downstream Filter tails and different-branch
release clocks. This is substantive **physical listening acceptance of the
slice-48 core lifecycle semantics**, not merely one nested-Instrument
fixture.

**Still separate:** exact internal retirement when final Volume permanently
reaches zero, repeated Note Off not resetting release origin, all scripted
curve variants, every Multi/Operator topology, precise anti-click/sample
limits, CPU/underrun stress and exhaustive platform coverage. The automated
tests remain the appropriate oracle for internal retirement and timing.
PR #12 remains draft/unmerged; completing slice-48 listening does **not**
complete the wider all-nonstructural-property Synth preview milestone.
