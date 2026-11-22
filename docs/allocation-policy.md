# Managed allocation policy for Heresy

**Status (2026-11-22): owner-approved engineering guidance, not a fully implemented analyzer or a blanket restriction on authored scripts.** Related future Pattern/Sequence work: [issue #58](https://github.com/logiclrd/Heresy/issues/58); future Instrument rework: [Script Instrument plan](script-instrument-rework-plan.md); editor tracing: [issue #49](https://github.com/logiclrd/Heresy/issues/49).

## Engine-owned paths

- **Hot path:** avoid **new managed allocations** in code that systematically executes more than approximately **100 times per musical row**. Per-sample, per-voice, high-frequency renderer and device-callback code is subject to the strongest scrutiny. Treat this as a workload-based engineering classification heuristic, **not** a programmatically enforced literal 100-call threshold.
- **Cooler path:** **try to avoid** allocations when a simple allocation-free alternative exists, but small, controlled allocations per Pattern/Sequence instance, musical row, note or control action are acceptable when the cost is negligible and the code is clearer or more reliable.
- **Safety and measurement:** review allocation counts/sizes and work-frequency rather than banning all allocations; use measured profiling and stress tests. Do not introduce unbounded growth, per-sample callbacks to the UI, or device-callback allocation. An occasional allocation elsewhere is not itself a performance regression.

## User-authored scripts

- **Script Patterns / Script Sequences:** future compiler work should permit user-authored managed allocation in these contexts. Keep restricted language/type access, deterministic sequencing, CPU/event budgets and memory safety deliberately constrained. See [issue #58](https://github.com/logiclrd/Heresy/issues/58). **Not implemented by this document.**
- **Future procedural Script Instruments:** explore a setup/initialization block and a per-note handler. Allocations in the setup block are **permitted without a warning**, because they can prepare state for reuse. Allocations in the per-note handler are **permitted but should trigger a non-blocking advisory warning**, encouraging reuse and moving work into setup. This is **future redesign guidance only** and does not change current three expression-based Instrument scripts. See [Instrument rework plan](script-instrument-rework-plan.md).
- Engine-internal hot-path requirements should not be mechanically interpreted as a universal prohibition against author-created objects. Resource/work budgets, runtime validation and appropriate diagnostics still apply.

## Editor-only F6 source highlighting

Source-call provenance metadata is only opted into for **Script-editor F6 auditions**, not ordinary playback or export. Its allocation rate is coupled to the **musical events/row/control actions**, not automatically to the high-frequency PCM sample loop, so modest per-action metadata allocations are **acceptable** under this policy. Avoid repeated allocations per PCM frame and unnecessary duplicates. Already implemented immutable snapshot reuse, chronological scheduled-event metadata and bounded published timeline entries should remain.

**Visibility nuance to verify:** the existing code enables tracing when F6 originates in a Script editor; do not equate that with a proven guarantee that no metadata is produced after that editor pane is subsequently hidden or closed. If visibility is a hard precondition, test that lifecycle and/or disable tracing after losing the editor consumer. Do not assert it without code/desktop evidence.

The [issue #49 completion checklist](https://github.com/logiclrd/Heresy/issues/49) records the decision on the original strict-allocation acceptance wording and any separate lifecycle follow-up. This document does **not** imply all heap allocations inside optional F6 tracing have been eliminated.
