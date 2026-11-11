# Definite and Indefinite Note Off — recursive sound lifetime

Status: implemented on draft PR #12; automated PCM/regression acceptance recorded
below. This is a Heresy sound-lifecycle contract, not an Impulse Tracker
compatibility rule. Physical listening remains unverified.

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
