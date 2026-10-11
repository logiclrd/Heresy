# Reusable filters — design contract

Status: **incremental implementation** (11 October 2026). The Core filter-type catalogue, parameter descriptors, Filter SongObject definition model, and independently configured stage instances/chains are implemented. The six-root Filters document tree and four-column Avalonia layout (including four-/five-root migration) are also implemented. Filter definition persistence, editor/creation UI, DSP and playback integrations remain outstanding in [todo.md](todo.md). Current code still has the tracker-compatible ResonantFilterState and the limited final-speaker low/high-pass filters; neither has been replaced.

## Intent and governing constraints

Provide reusable, serial FIR/IIR filter processing in FM graphs, Instrument tone specifications, scripted/data Pattern note events, persistent physical/virtual playback channels, and final speaker outputs. The same definition and parameter-description machinery must serve editors, native commands, script-produced commands, persistence, and rendering.

Preserve the production lazy incremental recursive renderer, frozen song snapshots, sample-accurate event ordering, deterministic replay/seek, finite bounded work, the realtime PCM producer/ring boundary, private-mixdown isolation, and the existing physical/flattened source ownership rules. No DSP design or coefficient regeneration runs in the SDL callback. Do not create pre-rendered song buffers or future-event journals.

## Filter catalogue

The selector contains nonselectable grouping rows (indented entries are children). The two principal sections communicate relative computational cost, not a runtime guarantee:

- **Infinite Impulse Response (IIR) (Cheap)** — nonselectable
  - **State Variable Filter (SVF)** — nonselectable
    - Low-Pass
    - High-Pass
    - Band-Pass
    - Band-Stop (Notch)
  - **Audio EQ Biquads** — nonselectable
    - Low-Pass (12 dB/octave)
    - High-Pass (12 dB/octave)
    - Low Shelf
    - High Shelf
    - Peaking / Bell EQ
    - Band-Pass
    - Band-Stop (Notch)
  - Ladder (linear)
  - Ladder (non-linear/saturated)
  - All-Pass (Phase Shift)
  - One-Pole (6 dB/octave)
  - Comb (feedback)
- **Finite Impulse Response (FIR) (Expensive)** — nonselectable
  - **Standard Arbitrary FIR** — nonselectable
    - Low-Pass
    - High-Pass
    - Band-Pass
    - Band-Stop (Notch)
  - Comb (feedforward)

Hilbert Transformer and Half-Band are excluded from this pass. Comb feedback is IIR; comb feedforward is FIR. Linear and nonlinear ladders are deliberately distinct selectable types.

The SVF menu is symmetrical with the biquad submenu, but its four response types may share one internal SVF implementation with an immutable response selector. A two-pole SVF low-pass is not necessarily a new transfer-function family versus a low-pass biquad: both can produce equivalent linear responses. SVF topology has different numerical and modulation characteristics, notably for fast cutoff/resonance modulation. Favor a stable topology-preserving/TPT formulation. There is no requirement to expose SVF response selection as an audio-rate graph input.

Each selectable filter type has an explicitly specified algorithm, numeric constraints, meaningful defaults, parameters, and response/tail tests before that type is marked implemented. Configure nonlinear ladder saturation/drive and oversampling policy explicitly; define all-pass order, and comb delay/feedforward/feedback gain and stability constraints. Avoid claiming all types cost the same.

## Implemented Core parameter contract (initial catalogue milestone)

Core entry points: `Heresy.Core.Filters.FilterCatalogue`, `FilterKind`, `FilterMenuItem`, `FilterParameterDescriptor`, `FilterParameterValue`, `IFilterSpecificationInstance`, and `FilterSpecificationInstance`. The 21 selectable kinds are represented by distinct enum values, while nonselectable category rows exist only in the menu. Descriptors are shared immutable schema objects with canonical property names, units, bounds, defaults, numeric-versus-enum type and continuous-versus-structural classification. Instances use case-insensitive descriptor lookup; choice input is canonicalized on assignment. Batch assignment rejects duplicates and validates before commit. This milestone intentionally does **not** introduce a `SongObject` Filter definition, serialization, DSP state or playback wiring.

Initial conventions (refine responsibly when implementing an algorithm; coordinate with its actual renderer):

| Parameters | Initial range/default | Filter uses |
| --- | --- | --- |
| `CutoffHz` | 1–20,000 Hz; 1,000 Hz | SVF, biquads, ladder, all-pass, one-pole, FIR low/high-pass |
| `Q` | 0.1–20; approx. 0.7071 | SVF, resonant biquads and peaking |
| `GainDb` | −36 to +36 dB; 0 | Shelf and peaking EQ |
| `ShelfSlope` | 0.1–2; 1 | Shelf EQ |
| `Resonance`, `Drive` | 0–1 / 0.1–10; 0.25 / 1 | Ladder; drive only nonlinear |
| `Oversampling` | 1x, 2x, 4x; 2x | Nonlinear ladder, structural choice |
| `Order` | 1 or 2; 1 | All-pass, structural choice |
| `Mode` | LowPass / HighPass; LowPass | One-pole, structural choice |
| `DelaySeconds` | 0.001–2 s; 0.02 s | Both combs; structural number |
| `Feedback`, `Feedforward`, `Mix` | −0.95–0.95 / −1–1 / 0–1; 0.5 each | Comb family |
| `TapCount` | odd integer 3–2047; 127 | Standard FIR, structural number |
| `TransitionWidthHz` | 1–5,000 Hz; 200 Hz | Standard FIR, structural number |
| `Window` | Hann / Hamming / Blackman; Hamming | Standard FIR, structural choice |
| `PhaseResponse` | Linear / Minimum / Maximum; Linear | Standard FIR, structural choice |
| `LowCutoffHz`, `HighCutoffHz` | 1–20,000 Hz; 400 / 4,000 Hz | FIR band-pass/notch; enforce low strictly less than high |

Current authored frequency limits are sample-rate-independent schema bounds; the eventual renderer must additionally enforce valid Nyquist/sample-rate constraints. A numerical structural property (e.g. `TapCount`) can be assigned through native commands but has no audio-rate FM input, just like enumerated properties. Band-edge cross-validation is atomic, allowing a simultaneous change to both bounds even where a one-at-a-time edit would be temporarily invalid. No DSP behaviour is claimed solely by selecting these initial metadata values.

## Object model and ownership

Three distinct layers:

1. **Filter definition:** a shared SongObject with stable ObjectId, an ordered nonempty list of filter specifications (stages). Each stage has a stable stage identity, unique user-visible name, and selectable filter type. Processing order is serial: output of each stage feeds the next. Names are unique within one definition under a case-insensitive comparison, and are valid unambiguous parameter-path identifiers; rename/reorder preserves stable stage identity.
2. **Filter specification instance / Filter instance:** a filter specification instance identifies one stage of a definition and holds that stage's configured parameter values, distinct from the definition's defaults. A Filter instance references a definition and supplies specification instances for every stage; multiple uses of one definition are independent. One use may have overrides while another uses defaults. A filter chain is an ordered collection of such filter instances/layers.
3. **Processing state:** one privately owned mutable DSP state for each actual processing invocation, stage, and PCM channel: FIR delay lines, IIR integrators/histories, coefficients, smoothing and optional tail accounting. Never share mutable histories between separate live notes or between independently routed speaker streams, even if they share a definition or parameter instance.

Filter definitions are distinct from configured uses, which are distinct from processing histories. Immutable prepared data (validated coefficient sets) may be shared only when safe. Do not store per-voice DSP history in the song. For nested Instruments, compose ordered filter *layers*, not one flattening scalar property: innermost tone processing first, then outer tone layers, then any explicit note-event filter, and finally the physical channel's persistent filter. A source's filter layers are not overwritten by adding an outer layer.

An instrument selecting another Instrument composes filters in this order. For flattened recursive Pattern/Sequence sources, project inherited note-level layers into independently processed descendant voices; retain their own histories and logical instigation/cancellation semantics, not a new independent clock. A deliberately combined nested mix must use the existing private mixdown, which owns its own channel context. Do not silently change flattening into mixdown merely to realize filters.

The interface to a filter specification instance must enumerate descriptors and get/set by the same canonical name. Descriptors specify name, value kind (finite numeric / enum / other explicit typed choice), units, range/validation, default, processing relevance, and whether the parameter is continuously modulatable or structural. Parameter name reuse across *different selectable types* is permitted; one instance has only one active type. Internal field storage may share fields across types without duplicating semantically identical parameters. Lookup by a stable stage ID is preferable in persisted graph links, while UI and command text use stageName.parameterName (e.g. stage1.CutoffHz). A stage rename must update or invalidate text/links explicitly, never silently retarget them.

Parameter changes and definition edits are validated atomically. In-place edits to a shared definition update the definition for all references in future snapshots while preserving independent per-instance overrides for still-compatible stage IDs/parameters. Removed/changed stages cause invalid graph ports/bindings to be surfaced and repaired, not silently reconnected. Editing an active song must not mutate an already-frozen playback snapshot.

## Implemented reusable definition and instance model

Core types: `Heresy.Core.Filters.FilterStageDefinition`, `FilterDefinition`, `FilterInstance`, `ConfiguredFilterStage`, and `FilterChain`. The definition extends `SongObject` and uses the reserved `SongObjectKind.Filter` identity. **Filter definitions can now be added to SongDocument**: the six-root Filters section and four-/five-root migration are implemented. Filter creation and editing are deliberately not yet exposed through the UI, because JSON, clones and song snapshots do not yet support live Filter song objects. Those tasks follow in the persistence and standalone editor milestones.

A new definition has one valid `stage1` One-Pole stage; all definitions must remain nonempty. Stages have positive integer IDs, validated unique case-insensitive ASCII identifier names (no ambiguous dots in parameter paths), an independently selected `FilterKind`, and a complete read-only validated set of default parameter values. The definition supports validated atomic `ReplaceStages`, add/remove/rename/reorder/type change and default updates. Automatically assigned stage IDs never reuse deleted IDs. Renaming or moving a stage does not change its ID. Each explicit stage type change advances a generation, so per-use overrides tied to the old type cannot unexpectedly reappear if the type changes back or a stage ID is removed/reintroduced.

`FilterInstance` references a shared `FilterDefinition` (also exposing its stable ObjectId), and stores only explicitly configured stage overrides. Effective stage parameters inherit current definition defaults unless overridden, preserving independence across uses; resolving stages follows the definition's current serial ordering and names. The stage-parameter getters supply *independent* mutable snapshots; writing to one returned parameter object cannot modify the definition or a different instance. Atomic batch updates validate the effective configuration before committing overrides. Copies do not share mutable overrides. `FilterChain` composes independently owned layers in **inner-to-outer processing order**, preserving recursive Instrument nesting without flattening filters into one set of parameters. Chains are configuration objects, **not DSP history**.

These models are Core-only authoring/definition types. No DSP or graph node is wired to them yet. Later persistence must store Filter instance overrides by stage ID and filter ObjectId, not by mutable stage index/name or raw in-memory reference. The sample-rate-dependent DSP state belongs exclusively to separate render-time processing instances.

## Parameter categories and structural changes

**Continuous parameters** can be driven by FM graph inputs and updated through commands; e.g. cutoff, resonance, gain, drive, or modulation-friendly delay, if supported by that algorithm. Recompute coefficients in a bounded, stable manner with suitable smoothing/control-rate optimization. Do not run costly FIR redesign for every frame.

**Structural parameters** are configurable in all parameter editors and command payloads, but have **no FM graph input port**. Examples include FIR tap count, window/design choice, Phase Response, topology/response choices, and other parameters where an audio-rate redesign is inappropriate. Phase Response for standard FIR has **Linear**, **Minimum**, and **Maximum** values. Coefficient preparation for minimum phase and maximum phase occurs only when configuration is constructed or structurally changed. FIR designs are constrained by active sample rate and must have bounded tap/resource limits; define stable behavior for invalid or out-of-range cutoffs.

Changing only coefficients or response output while internal state topology is compatible need not clear DSP histories. A structural change incompatible with previous histories (tap count, topology, delay layout, phase mode where the implementation requires new state, etc.) **implicitly replaces/resets that stage's DSP state at the change's scheduled frame**. All fields and their state transition commit atomically. This may produce a discontinuity; possible future crossfades are not required for v1. Set/clear of an entire Filter object always starts with default/configured values and fresh histories; reselecting the same object is still a fresh selection. Parameter changes to an otherwise selected compatible filter preserve histories.

All assignments must be finite, in validated units. Textual enum values are accepted for structural settings. Structural parameters are not restricted from native parameter-setting commands; such commands may rebuild a stage and swap state atomically.

### Parameter-configuration assistance

Both the instance inspector and the native command editor must offer type-aware guidance/wizard controls derived from the *selected stage's parameter descriptors*: appropriate filter response, frequency/cutoff in Hz, resonance/Q, transition width, FIR taps/window/phase, and compatible option sets. Show estimated cost/latency and range constraints where useful. Entering a user goal such as “low-pass at 2500 Hz” can produce a complete valid parameter set, including mutually dependent structural settings. A helper/assistant must not invent fields unsupported by the stage's selectable type; changing the stage *type* itself remains an edit to the shared Filter definition (or requires selecting a different definition), not an incidental parameter-command side effect.

The same assistance is available for the effect editor and converts its result into editable text, rather than bypassing the validated command model.

## Native assignment text and comment replacement

SetChannelFilterParametersCommand and SetNoteFilterParametersCommand accept a set of typed assignments, authorable as multiline text. Syntax: one stageName.parameterName = value per assignment, allowing whitespace; numerical literals use invariant culture, enumerations use canonical names. Empty lines and comment lines beginning with # (optionally indented) are ignored by command execution. Example:

    # stage2: Low-pass filter, 2500 Hz cutoff
    stage2.CutoffHz = 2500
    stage2.Q = 0.7071067811865476

If a helper rewrites stage2, remove **all existing stage2 assignments and all # stage2: comment lines**, then insert one fresh annotated block. Preserve other stages' assignments/comments and unrelated comments. Match stage paths exactly rather than by prefix (stage2 must not match stage20), ignoring case consistently with the stage-name policy. If an old stage2 block existed, replace it in place near its first occurrence; otherwise append. Regenerating the same stage repeatedly must not accumulate obsolete annotations. The assistant may show raw calculated coefficients if the selected filter type actually exposes those parameters; use semantic names like CutoffHz and Q when available, rather than pretending every implementation exposes generic A/B coefficients.

All assignments in a command are validated together against the selected definition, with duplicate paths either rejected or explicitly canonicalized before committing (use rejection to prevent ambiguous ordering). On any unknown stage, unavailable parameter, invalid type or value, absent selected filter, or invalid combination, leave the **entire command unapplied** and emit a rate-limited diagnostic. No partial stage updates. Parsing and validation errors are surfaced in the editor as well. Omitted parameters retain their current values. A command need not overwrite every property, including structural properties.

## FM graph integration

Add a Filter node with one audio Input, an output, a Filter-object dropdown (including italic New... creating an object in the Filters tree), and **Edit Definition...** beside the selector when nonempty. New... and definition editing follow the shared-object workflow used by graph Envelope nodes. A selected definition projects editable instance controls for every parameter of every stage, named stage.parameter, in serial stage order.

Each continuously modulatable numeric parameter becomes an optional left-edge input port (along with Input), increasing graph node height as needed. Disconnected ports use the instance's configured numerical values; connected ports supply their per-frame values. Structural/enum parameters remain editable but do not become ports. Persist graph connections by stable port identity when practical; adding/removing/renaming a stage must never retarget a connection silently. Do not allow a graph cycle.

Each FM note owns its own filter histories; graph evaluation follows dependency order, including connected parameter inputs evaluated before that Filter node's sample. Split-frame rendering, seek/replay and snapshot isolation must reproduce the same PCM. A filter's output is a normal graph scalar; Input's absence and an unassigned Filter object need a deterministic neutral/bypass behavior and must not crash.

**Tail** is a nonnegative, finite **seconds** property on each FM Filter graph node. It is a manually specified *additional allowance*, not an inferred exact physical impulse-response duration. After the rest of that FM graph has established a finite natural termination boundary (including Note Off and envelope release), add the **maximum Tail of all relevant reachable Filter nodes**, not their sum. An unbounded envelope remains unbounded; Tail cannot manufacture a finite lifetime for it. Continue evaluating filter memory over the extra frames while terminating normal note excitation at the computed base boundary; otherwise an ungated oscillator would keep generating a signal rather than letting a filter tail decay. Stop at the exclusive extended boundary. Serial filters may mathematically require a longer combined decay than max(Tail); the user explicitly controls this budget, so document that limitation, do not silently sum it. Nonexistent/zero Tail adds nothing. Test no-envelope, gated-envelope, frequency-only-envelope, dry-path and multiple-Filter-node cases, plus finite post-release silence.

## Instrument tone filtering and note-event layering

Both data-table and scripted ToneSpecification records gain optional Filter definition selection (or None) and an independent Filter instance configuration. The tone grid adds a Filter selection column plus a “...” instance editor, enabled only when a definition is selected. It now has ten columns including the previous four envelope-reference columns; instrument table interning/copy-on-write must consider filter reference **and full parameter instance data**. The matching Script Instrument editor uses the same tone data. “Edit Definition...” only edits the shared definition.

Note starts (including script-produced notes) can carry an optional *note-event filter configuration*. When absent/None, that new note starts without an event-local filter — no previous note's local selection leaks into it. When present, this is an additional outer layer after recursively inherited instrument-tone layers. Its DSP state belongs to the note's physical/descendant voice(s), not to the persistent channel. A displaced or released voice retains its entire existing per-note chain. Ordinary new-note displacement/Off/Cut/Fade and flattened logical-source cancellation retain their established semantics.

Two native effects address the current note-event filter layer, without overwriting inner Instrument tone layers:

- **SetNoteFilterCommand**: select or clear the current note's filter configuration (fresh state on selection).
- **SetNoteFilterParametersCommand**: atomically update its specified stage parameters.

A StartNoteCommand carrying an optional filter initializes that note's event layer; same-frame follow-on note-filter effects act on the just-started note according to stable emission/command order. With no current logical note, note-target effects do nothing and emit rate-limited diagnostics. A flattened logical instigating note forwards applicable note-filter changes to its descendant voices under its existing ownership controller; it must not mutate unrelated sibling voices. Private mixdown retains independent timing and channel state.

## Persistent channel filtering, native effects, envelopes and NNA

Every physical/virtual playback channel conceptually has **two distinct filter slots**: a persistent *channel-output* filter and the *current note's* event-local filter (the latter belongs to the current note/voice and naturally migrates with it). Existing Instrument filters are deeper per-note chain layers.

Channel filtering processes the channel's combined voice output, after its note/Instrument filters, before adding to the playback session's speaker mix and before final speaker filters. There is **one channel-output filter per actual host channel**, not one per flattened-scope level. A flattened child event that changes channel filtering acts on the root hosting playback channel; its selection, parameters and envelope bindings **persist after child termination or cancellation**. Scoped channel volume/effect memory continues to behave as before; these filter commands are an intentional exception. A private mixdown creates its own isolated channel filter banks and state, not shared with its parent.

Native effect command types (set/clear means a nullable Filter reference):
- **SetChannelFilterCommand**: select or clear persistent channel-output filter, starting fresh state on selection.
- **SetChannelFilterParametersCommand**: atomically assign any subset of selected channel-stage parameters.
- **SetNoteFilterCommand** and **SetNoteFilterParametersCommand**: as above, for current note layer.
- **SetChannelFilterParameterEnvelopeCommand**: attach/replace/detach an Envelope reference for one *non-structural numeric parameter* of the current channel-output filter.
- **SetNoteFilterParameterEnvelopeCommand**: same operation for a non-structural numeric parameter in the current note-event filter.

Envelope bindings use stage.parameter paths and reference the existing Node or Script Envelope objects. At attachment, their independent clock starts at the command's frame. The authored envelope output is interpreted as an absolute parameter value **in the parameter's units**, not an implicit 0..1 clamp; the descriptor's supported range still applies. When bound, the envelope output temporarily overrides the stored base parameter, and detaching it exposes the base value again. A base assignment while an envelope is bound changes the stored base, not the envelope's current value. Note-level envelope clocks receive Note Off and terminate with their note; channel-level envelope clocks persist while bound, with no implicit Note Off when an unrelated note ends. Invalid/nonfinite evaluated values must fail safely with diagnostics and preserve the last valid value. Bindings to a stage/parameter removed or made structural by definition edits are invalidated explicitly. Process same-frame select/clear, parameter assignment and envelope attach/detach commands strictly in their existing NoteCommand emission order. A command acts on the state established by preceding commands at that same frame; do not reorder user-authored operations into artificial command-class phases. Envelope evaluation and parameter changes must remain chunk-invariant.

The current ToneSpecification.FilterEnvelopeId and IT filter/effect behavior refer to **Heresy's existing tracker-compatible per-voice resonant filter** and are not to be silently reassigned to this new general-purpose filter system. Preserve those semantics unless a separate explicit retirement/migration milestone is approved. Do not accidentally apply the old filter twice.

On NNA displacement to a dynamic/virtual host, **deep-copy the originating channel-output filter configuration AND all active DSP/envelope state**, at the exact displacement boundary, into the virtual channel. Its next output frame must continue the same response without a freshly reset filter. The physical channel also retains its own state for future notes. These are independent copies thereafter; subsequent changes to the physical channel must not affect the displaced voice. Its event-local and Instrument tone filter chains move with the voice, without being copied/reset unnecessarily. Forked histories may naturally lead to differing subsequent tails, and are not a mathematical separation of earlier mixed audio; verify audibly and by PCM tests.

## Final speaker-output filters

Replace the current limited per-speaker None/LowPass/HighPass implementation with the same reusable engine and configuration/instance semantics, retaining the **exact same placement: once after the final session's master mix** (never for each private mixer). Maintain independent history for each speaker. A user with the present one-pole low/high-pass settings should be able to express their result through the new One-Pole configuration; provide an intentional transition of existing audio settings rather than silently discarding them. No promise of an unnecessary older-song schema migration. Whether speaker configuration stores a frozen inline instance or refers to an available Filter object must not make an audio device setting inadvertently depend on a live song object; the concrete persistence/UI ownership choice is part of this integration milestone.

## Rendering, tail and runtime invariants

- DSP preparation respects sample rate, sample count, output channel count and bounded CPU/memory budgets. FIR convolution, state-variable operations and nonlinear oversampling must never use unbounded work. Guard against unstable IIR feedback, NaN/Inf, denormal buildup, invalid high frequencies, and nonphysical parameter ranges.
- Distinguish natural source end from filter residual output. FIR has a finite mathematical delay tail; IIR may decay asymptotically, requiring a documented silence threshold and a bounded drain/retirement policy. A channel filter must be allowed to drain after its input ceases; an active script producing indefinitely is not falsely declared finite. FM graph's explicit Tail is an additional authored lifetime limit as defined above.
- Process channel buffers in a fixed deterministic order; render-block partitioning must not change output. A seek backwards reconstructs immutable preparation and histories by deterministic replay (or later justified bounded checkpoints), never by sharing live states or caching an entire future event stream.
- Reset/rebuild a stage only when its state topology becomes incompatible; preserve compatible state on normal cutoff/Q/gain changes. Native filter changes are event-frame atomic. Freeze live document edits from current playback.
- Preserve channels/spatial output, deterministic diagnostics with rate caps, no crashes on malformed scripts/commands, and existing NNA, pattern preview, Note Off, virtual voice and mixdown rules.

## UI placement and editing scope

Use a **four-column Avalonia document grid**, not twelve: first row has Sequences and Patterns, each spanning two columns; second row has Patches, **Filters**, Envelopes, Instruments each occupying one column. Filters is a sixth fixed semantic tree root with standard folder/reorganization, New, Rename/Delete, and a dedicated editor showing ordered named stages, filter type selector with nonselectable section rows, stage reordering, type-aware parameter forms, and a way to add/remove stages. Definitions require at least one stage and unique names.

In the FM Filter inspector, **Edit Definition...** opens that shared editor; in an Instrument tone row, “...” opens that use's instance parameter editor, not the shared definition. New... creates the shared object in Filters. The textual native effect editor uses the same descriptor-based help, emits commented configuration blocks, and exposes parse/validation diagnostics without applying a partial command. Protect simultaneous editor drafts against overwriting updates made from another view, following the existing Envelope shared-editor approach.

A six-root file must be distinguished from five- and four-root historical layouts on load, while preserving stable IDs/tombstones/folders. Retain persisted v1 where a deterministic compatibility extension is sufficient; introduce a genuine migration only if the new serialized schema actually requires one. Clones, imports, snapshots, script references, deleted objects and audio/document revision accounting all need coverage. Update documentation of [document panes](document-panes.md), [instrument tone grid](instrument-tone-grid-model.md), [audio output](audio-output.md) and [effect operators](effect-operators.md) when their implementations change.

## Completion checks

For each independently completed milestone in [todo.md](todo.md): add Core validation and persistence regressions where relevant; headless Avalonia authoring tests; actual PCM tests for IIR/FIR spectral response, impulse and stability, tail behavior, modulation, chunk invariance, reset transitions, same-frame ordering, recursive nesting, per-note isolation, NNA state-copy continuity, private-mixdown separation, and deterministic seek. Validate the *final SHA* in CI, and keep real Windows/Linux listening/graph UX checks separately identified rather than claiming CI proves hardware audio.
