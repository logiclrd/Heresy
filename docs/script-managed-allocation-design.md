# Script Pattern and Script Sequence managed allocations — discovery and design proposal

**Status:** Discovery for [issue #58](https://github.com/logiclrd/Heresy/issues/58), not a compiler change and **not an approved allocation API**. Based on the repository at `7bbc256356a60e0e7240622108007add2578f4cf` (2026-11-22 local). Preserve the separately approved [engine allocation policy](allocation-policy.md). Feedback and approval are required before admitting new authored forms.

## Goal and boundaries

Allow user-authored data allocation in **Script Patterns** and **Script Sequences** without relaxing engine hot-path allocation discipline, bounded execution, streaming order, deterministic replay, or script isolation. A user may legitimately want a small lookup table or temporary data structure per Pattern invocation or Sequence order. This is different from creating objects in the PCM renderer, and distinct from the future Script Instrument setup/per-note design.

Allocation permission **must not** mean unrestricted C#, arbitrary constructors, arbitrary framework methods, or a blanket exception from resource budgets. Preserve `#49` editor-F6 provenance and the future `#54` ownership work.

## Current implementation inventory (verified)

| Area | Current behavior | Implication |
| --- | --- | --- |
| Syntax validator | `ScriptCompiler.ValidateRestrictedSyntax` rejects explicit, implicit and anonymous `new` objects; explicit/implicit array creation and `stackalloc`; strings and interpolated strings; `using`, `foreach`, lambdas, LINQ, `try`, `throw` and other constructs. | No user managed arrays or objects can currently be created. Merely removing these cases would admit unsafe/unreviewed forms. |
| Semantic validator | `ValidateRestrictedSemantics` allows invocations only on the relevant Pattern/Sequence base helpers, `System.Math`, and four selected pitch-math helpers. It rejects other member access except math and selected enum constants. | Allowing a safe `new T[]` requires a type allowlist, and `array.Length` or future collection methods would need narrowly scoped semantic rules. Construction is **not** equivalent to permitting arbitrary instance methods. |
| Compilation pipeline | `PrepareCompilation` is shared by Pattern, Sequence, Envelope, Instrument and parametric expression scripts. `AnalyzePatternSource`/`AnalyzeSequenceSource` and eager/incremental compile APIs all call it. | Carry an **explicit authoring context** into both syntax and semantic validation. Do not change restrictions for the other six script contexts accidentally. |
| Pattern generator | Eager and resumable Pattern compilers use different wrappers. Incremental event helpers must be direct expression statements and each must suspend immediately afterward; editor-F6 instrumentation rewrites full original callsite spans. | Allocation expressions must not introduce invisible note emissions, nested note calls, change yield frequency, or remap authored spans. Tests must cover eager/incremental equivalence. |
| Loop work budgets | `LoopCheckpointRewriter` checkpoints `for`, `while`, `do`; the incremental Pattern form cooperates every 128 iterations. `ScriptExecutionBudget` limits expansion units and samples a 10-second runaway guard. | These guard **work/time**, not heap byte counts. Allocations must be charged at a trusted runtime boundary, including loops that allocate without emitting events. |
| Script lifetime | `PatternScriptProgram` is instantiated for each Pattern invocation. `SequenceScriptProgram` is reused across entry lookups, with a fresh execution budget and fresh local variables per `GetSequenceEntry`; seeded RNG persists as defined. | Distinguish Pattern-invocation accounting from per-Sequence-lookup accounting. Do not create hidden static storage or leak per-order arrays between lookups. |
| Runtime/API references | `ScriptEditorHelp` currently says no allocations/strings/foreach. `docs/script-programmability.md` documents restricted, bounded scripts; `docs/allocation-policy.md` already records the new owner policy as future work. | Update only the Pattern and Sequence help sections after implementation. Keep actual contract and documentation aligned. |

## Proposed first implementation slice (requires approval)

**Start with a deliberately small, useful managed allocation:** a *single-dimensional, zero-based array of supported primitive value types*, created using explicit-length syntax:

```csharp
// Example candidate, not currently valid Heresy script syntax:
double[] pitches = new double[16];
for (int i = 0; i < pitches.Length; i++)
    pitches[i] = Semitone(i);
Note(0, 0, _O(17), pitches[4]);
```

In a Script Sequence, a similar local table could choose a `Play` target or starting row. This offers actual managed objects without enabling arbitrary third-party code or hidden dynamic resizing.

Suggested **initial type allowlist:** `bool`, `int`, `uint`, `long`, `float`, `double`; include other integral primitives only if tests justify them. Exclude reference types, `string`, multidimensional/jagged arrays, generic framework collections, custom object constructors, reflection, `Span<T>` and pointers at this stage. Array indexing and `Length` can be allowed only on the approved array types. Arrays of core object IDs and enumerations are a separate review item, rather than implicitly approving all referenced framework types.

The array length may be a computed integer, subject to **runtime** validation and a per-allocation size ceiling. Do not require authors to use a constant-only length: it would defeat useful computed tables. A first slice may omit initializer syntax (`new[] { ... }`, `new double[] { ... }`) until its pre-allocation charging strategy is specified. In particular, validating the size *after* the CLR has already allocated an initializer is insufficient.

### Memory and execution accounting

- Charge the **requested array size before allocation**, using a generated, trusted call (e.g. `CheckedArrayLength<T>(expression)`) that author scripts cannot call themselves. Keep the original expression evaluated exactly once, preserve source coordinates in diagnostics and avoid altering musical command order. Block negative/overflowing/oversized requests with `SequencingResourceLimitException` rather than allowing a huge allocation followed by a late check.
- Track a **cumulative charged-byte budget** for a Pattern invocation and independently for each Sequence lookup, not just simultaneous live bytes. A discarded array in each loop iteration must still consume the allowance. This is a conservative upper bound on allocation churn, not a claim of precise managed heap accounting or GC live-set measurement.
- A practical first version should set **two documented limits**, per-array bytes and per-invocation-or-lookup cumulative bytes. Values remain **open for owner approval**; consider 1 MiB per array and 32 MiB cumulative as starting hypotheses for tests, *not* established contracts. Account for array headers/element sizes conservatively; `checked` arithmetic and monotonically increasing counters must not wrap.
- Ensure the trusted charging path is not bypassed by alternative syntax or implicit array creation. Disallow all uninstrumented allocating syntax (including `new` for other types), and restrict any allowed helper to audited types/methods that cannot allocate unconstrained objects indirectly.
- Use existing exceptions/diagnostic propagation, but add targeted messages with attempted/remaining sizes and authored source locations. No silent fallback to empty arrays, truncated arrays, dropped notes or reordered playback.
- The existing 10-second safeguard and expansion checkpoints are **not** a substitute for byte budgets, and conversely byte budgets are not a substitute for CPU cancellation. Avoid adding per-sample audio work.
- Accept that legitimate scripts may generate large allocations intentionally and will receive a clear resource-limit error when crossing an explicit safety ceiling. That is distinct from arbitrary F6 **presentation queue** caps, which remain prohibited by the owner-approved lossless event contract.

### Compiler safety rules

- Pass an explicit context such as `Pattern`, `Sequence`, `Expression` into validation, using the same context in analysis and both eager/incremental compilation. Keep the current warnings/diagnostic source mapping, trusted editor-only callsite overload protection, and original restricted invocation/member allowlists.
- Use Roslyn semantic **symbols**, not matching text like `new double`, to validate target array types, ranks, element access and `Length`. Reject aliases, nested arrays and unapproved element types regardless of spelling.
- Keep `using`, `try`, `foreach`, user classes, lambdas, LINQ and arbitrary instance member calls forbidden in this slice. The separate `VirtualNote`/`NoteGroup` #54 work will introduce carefully reviewed object-method and disposal permissions; do not preauthorize them here.
- User script code should not be able to create, name, access, replace, or reset the trusted allocator/accounting state. Never expose arbitrary constructors or methods on `ScriptExecutionBudget`.
- Review generated source rewrites around `#line` remapping, loops, scope, and incremental `yield return`. Reject unsafe forms early with authored-span diagnostics.

## Verification matrix for implementation

1. **Allowed Pattern forms:** constant and computed lengths, indexing/writing/`Length`, array use to calculate `Note` pitches; test eager and incremental output identity and deterministic replay with seeded `Random()`.
2. **Allowed Sequence forms:** per-lookup array data affecting `Play` result, repeated lookups, loop checkpoints, and order-to-order RNG behavior; verify per-lookup accounting resets without persisting arrays.
3. **Rejection:** giant/negative/computed lengths, products that overflow size arithmetic, excessive total churn in a tight loop, multidimensional/jagged/reference arrays, `new object()`, `new List<T>()`, strings, reflection and unrelated framework methods; assert precise authored error spans.
4. **Isolation:** Instrument/Envelope/parametric expression analyzers continue to reject allocation; generated-only budget/provenance methods are inaccessible to authored scripts.
5. **Audio correctness:** Pattern raw events and Sequence entries remain ordered and unchanged versus allocation-free equivalents; F6 traced callsites still highlight original full calls; non-script export PCM is unaffected.
6. **Resource recovery:** invalid or pathological scripts report bounded failures through established authoring/playback diagnostics, without device-callback blocking or retained per-lookup allocations.
7. **Documentation:** update `ScriptEditorHelp` Pattern/Sequence reference and `docs/script-programmability.md` to describe only supported syntax and real limits; remove blanket “no allocations” statements **only for those two contexts**.

## Later expansion decisions (not implemented here)

- Array initializer syntax, strings, immutable tuples/records, finite resizable buffers, and a small curated collection API need separate resource-accounting and method-safety designs. Standard `List<T>` growth via `.Add` and `Capacity` would need charging and a hard capacity limit; unfiltered BCL methods are not safe to expose.
- Object lifetime and `using` in #54 must integrate with allocator budgets but have distinct **musical lease** semantics. `Dispose()` on a virtual note must not act like a managed-memory deallocation or an audio Off.
- The future Script Instrument setup/handler distinction and advisory allocation warnings remain entirely out of scope. No per-note warning is proposed for a current Script Pattern/Sequence.

## Questions for design sign-off

1. Is a narrow primitive-array slice useful before richer collections or do you want an approved bounded collection type in the first slice?
2. What per-allocation and per-Pattern-invocation/per-Sequence-lookup budgets best fit intended scripts? The provisional 1 MiB / 32 MiB limits above are suggestions only.
3. Should computed array lengths and simple `Length`/indexed writes ship together (recommended), while array initializers wait for safe charging support?

**Next action after approval:** implement context-scoped validation and pre-allocation runtime instrumentation in a separate production PR, with the verification matrix above. This document does not mark #58 implemented or relax any current authored restriction.
