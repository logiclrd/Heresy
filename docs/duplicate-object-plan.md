# Same-song Duplicate Object — shallow/deep specification and implementation plan

**Status:** User-refined contract captured 19 November 2026; D1 and D2 shallow duplication are implemented headlessly on draft PR #38. No deep-clone implementation or UI activation yet; PR must not merge before the remaining slices and owner acceptance.

**Scope:** All song **objects** in the six main-window song-tree panes. The Samples/"Patches" pane includes both Samples and FM Synths. Folder duplication is a separate concern. Unlike cross-song Import Synths, this feature duplicates within the same song.

## Context-menu contract

**Duplicate** always performs a **shallow clone**: a single new object definition with its own ID and independent *owned authoring data*. All references to other song objects still point to the original referenced objects, including references written as `_O(id)` in scripts.

**Duplicate (deep)** performs a **graph-aware clone** when the root object has meaningful recursive dependencies. Only the object types in the table below expose this second menu item. In a deep clone, every original object selected by that type's traversal rule is copied at most once, and all references *within the cloned subgraph* to any copied target are translated to its new ObjectId. Originals and references from objects **outside** the cloned subgraph remain unchanged.

| Root runtime type / list | **Duplicate** (all types) | **Duplicate (deep)** menu / traversal |
| --- | --- | --- |
| Data Sequence | Copy the sequence and its entries, retaining original Pattern IDs | **No** deep menu; Pattern entries are not traversed |
| Script Sequence | Copy script source verbatim; retain `_O(id)` target IDs | **Yes**, recursively follow referenced objects in its script |
| Data Pattern | Copy owned grid, notes, effects; retain referenced sound sources and Filters | **No** deep menu; sound-source/Filter references are not traversed |
| Script Pattern | Copy script source verbatim | **Yes**, recursively follow referenced objects in its script |
| Instrument | Copy tone specifications and tone table; retain referenced sources, Envelopes and Filters | **Yes**, recursively follow **tone source** references (`ToneSpecification.SourceId`) only; ordinary tone Envelope and Filter uses are not independently traversed |
| Script Instrument | Copy scripts and independently cloned tone specifications, retaining all original IDs | **Yes**: as a scripted object, recursively follow **all** typed references in tone specifications (sources, Envelopes, Filters), plus semantically recognized script references in `Source`, `PitchExpression`, and `NnaExpression` |
| Node Envelope | Copy nodes, sustain mode/indices, viewport limits | **No** deep menu |
| Script Envelope | Copy `Source` and release metadata verbatim | **Yes**, recursively follow semantically recognized `_O(id)` references in `Source` |
| Filter | Copy stage/default/identity state | **No** deep menu |
| Sample | Copy loop/spatial metadata and independently owned encoded asset bytes; immutable PCM may be shared | **No** deep menu |
| FM Synth | Copy graph nodes, positions, routes, editing settings; preserve external object IDs | **Yes**, recursively follow all external object references **from the graph**, including sound-source nodes (Sample, Synth, Instrument, etc.), Envelope nodes and Filter configurations; nested referenced Synths/Instruments/scripts follow their own deep-clone rules |

**Scripted-object override:** A `(deep)` menu item is always present for **every scripted type**, including Script Sequence/Pattern/Envelope/Instrument, even if the current script has zero referenced objects. If there are none, the resulting deep operation creates just one independent definition and is otherwise equivalent to shallow. Adding a new scripted type must opt into the same policy; the UI should not infer it solely from `SongObjectKind`, because multiple runtime types share the same kind.

**Per-type traversal is intentional:** A Data Pattern or Data Sequence is a *terminal definition* for deep traversal even though its authoring data contains references. An Instrument follows its tone **sources**, not its ordinary tone Envelope/Filter metadata (except for the separate Script Instrument override). A Synth's graph references traverse its external nodes, and any reached object applies its own rule. This prevents a deep clone from becoming an unconditional "clone every object transitively mentioned anywhere" operation.

**Unified remapping:** If a target is cloned for *any* reason, references to it from **every other cloned object** are remapped, even if a particular reference edge did not itself trigger traversal. Uncloned targets continue using original IDs. For example, if a Synth graph clones Envelope E and also reaches Instrument I whose tone happens to use E, I's copied tone can point to the **one duplicated E**; the Instrument edge alone would not have caused E to be cloned. This is the required "one duplicate used in all places" behaviour.

## Graph closure and cycle safety

1. **Discover without mutation:** Start from the selected root; walk only the type-authorized outgoing dependencies. Track discovery states per **original ObjectId** (unseen/discovering/discovered), and stop expansion at already-seen objects. Self-references and cycles (notably via scripts, which may bypass editor-time cyclic-graph prevention) must terminate normally. Preserve original graph topology; do not silently break cycles or recursively construct on the call stack without a visited map.
2. **Reserve provisional mapping first:** After the full reachable set is known, create a deterministic, collision-free `original ObjectId -> new ObjectId` map **for every included object**, including the root, before constructing any copied definition. Repeated graph edges, diamond graphs, self-edges and cycles always share exactly one mapped ID. The destination song is not modified or allocated during preparation. Provisional ID range must be validated for overflow, tombstones and intervening document edits.
3. **Materialize detached copies:** Clone independent owned authoring state; remap all references pointing to IDs in the map, while leaving other references untouched. Apply the same mapping to Script `_O(id)` references, FilterInstance definitions, FM graph nodes, nested Instrument tone specs and other supported typed references. Any new object or reference field added to the schema needs an explicit duplication policy.
4. **Propose names as one batch:** Use the established terminal-number progression, case-insensitively against existing definitions **and against names assigned earlier in the same batch**, per object kind. Root and dependencies each get their own new identity and sensible name; manual duplicates elsewhere remain valid.
5. **Validate, then commit atomically:** Confirm the source song's authoring fingerprint and ID cursor remain current. All clone definitions, encoded Sample bytes and proposed tree placements must be prepared before mutation. Any unsupported object type, missing target, unreliable semantic script analysis, uncopyable sample asset or invalid reference remap must fail clearly without partially adding objects, changing IDs or altering the source. Use one complete batch transaction and one coherent undo/redo unit.

**Cycle examples for tests:** `Script Pattern A -> _O(B) -> Script Sequence B -> _O(A)` clones A and B exactly once and rewrites both references; `A -> A` maps to the new A; `A -> B, A -> C, B -> D, C -> D` clones D once and makes B/C use the same cloned D. A deep clone must not inadvertently rewrite references in original A/B/C/D.

## Script-analysis and rewriting requirements

Existing, verified infrastructure:
- `ScriptReferenceAnalyzer.Analyze(source)` (Roslyn) recognizes semantic, literal `_O(id)` calls, reports **source spans**, excludes comments/strings and shadowing of the intrinsic, and marks malformed syntax/object calls as unreliable.
- `RoslynScriptObjectReferenceAnalyzer` adapts this discovery for Core.
- `SongReferenceAnalyzer` currently includes script edges only from **ScriptPatternDefinition** and **ScriptSequenceDefinition**. It does **not** yet extract Script Envelope/Instrument expressions, and its general reference collection is not a complete deep-clone traversal for FM graph Filter nodes. The existing script analyzer **does not provide a rewriting API**.

Consequently, **do not assume existing document-wide analysis alone is sufficient.** Deep-duplication discovery must explicitly cover **all** applicable script fields (Script Envelope `Source`; Script Instrument `Source`, `PitchExpression`, `NnaExpression`; Script Pattern/Sequence `Source`) and ordinary strongly typed per-object references. A Roslyn-aware, span-based rewriter must translate **only recognized intrinsic reference IDs** using the finalized old-to-new map; do not perform broad string/regex substitution on script text, comments, strings or user-defined `_O` methods. Re-analyze transformed source to ensure the remapped references agree with the mapping and all originally recognized references are accounted for.

If analysis is unreliable, a reference is malformed/nonliteral, or a reachable referenced object is missing, **refuse the deep operation atomically** with actionable diagnostics instead of silently making an incomplete duplicate. Shallow duplication preserves script text unchanged and need not traverse its dependencies. The supported, persisted reference syntax is `_O(<literal nonzero uint32>)`; arbitrary dynamically computed object IDs are not promised to be discoverable/rewritable.

## Shared invariants (both modes)

- **Definition vs reference:** Duplicate never changes the original definition or its external users. Deep only rewires references *inside its new cloned subgraph*.
- **Independent owned mutable data:** Do not share a mutable PatternCell, note/effect record, Instrument tone specification, configured FilterInstance, Filter stage definition, editable FM graph node, graph position/routing collection or pending encoded Sample byte container between originals and duplicates. Sharing immutable values/decoded Sample PCM is allowed.
- **Sample encoding:** Each duplicated Sample gets detached encoded bytes that can be saved in either `.hm` or `.hm.json`; it must not merely alias a mutable external filename. Source encoded data must be re-read and validated at acceptance before copying; source PCM is immutable and hashed once at construction. Preserve loop, channel positions, colour. A deep graph that reaches a Sample includes exactly one copied Sample.
- **Visual metadata:** Preserve existing source colours, FM editor positions/routes and Filter Batch state, Pattern dimensions/highlight rows, Envelope viewport, Filter stage allocator/generation metadata.
- **Placement:** Put the **root duplicate** adjacent to the specifically selected tree occurrence in its existing folder, and select it. For non-root deep duplicates, proposed default is to place one new tree entry next to a deterministic first existing placement (fall back to their kind's section root if none). Organizational placement does **not** create additional object copies; the root selection remains primary. This dependency-placement detail can be refined during the UI slice.
- **History:** One atomic whole-subgraph edit should undo/redo as a unit, with staleness/fingerprint guards. Do not silently reuse the UI's existing one-level, Synth-import-only history without intentionally extending its user-visible scope and state rules.
- **Regression coverage:** Per-runtime-type shallow/deep menu eligibility, exact owned-state fidelity, preserved and remapped IDs, shared targets, cycles/self-edges, scripted semantic spans and rejection cases, batch names, atomic rollback, undo/redo, nested tree folders, `.hm`/`.hm.json` save/reopen and Sample audio playback.

## Updated implementation slices

**D1 — detached shallow model, name proposals and atomic root-level commit foundation (complete on draft PR #38):**
- Data/Script Sequence, Data/Script Pattern, Node/Script Envelope and Filter cloning currently produce exactly **one** independently identifiable object with all source IDs unchanged, matching **Duplicate** semantics.
- Pattern cells and note/effect records are newly constructed; Filter stage IDs/generations and Envelope sustain/viewport are preserved.
- Candidate creation is detached; current `SongDocument.CommitImportedObjects` provides guarded atomic commit and undo/redo receipt. Temporary headless commit placement is the section root.
- **D1 code does not implement deep cloning and is unchanged by this specification refinement.**

**D2 — complete shallow runtime type coverage (implemented):**
- Samples use `SampleDefinition.CreateImportedCopy` with a new independently owned pending encoded asset and the already immutable source PCM. Encoding is reread at prepare **and again immediately before acceptance**; stored encoded SHA-256 (if present) and freshly decoded PCM must match the source's loaded PCM (cached hash then bitwise samples). A stale/corrupt/missing encoded source refuses the operation with no document mutation. `.hm` synthetic entry paths are read via `ExternalAssetIntegrity.OpenRead`. Copies preserve loop, source spatial positions and colour, and can save independently without relying on the old asset path.
- Data Instruments and Script Instruments get separate ToneSpecification instances and tone tables, and independently cloned `FilterInstance` override state. References to their original sources/Envelopes/Filters stay unchanged; the Filter **definitions** remain shared. Scripts (`Source`, `PitchExpression`, `NnaExpression`) are copied verbatim for shallow duplication.
- Synths reconstruct **every graph node** (including disconnected nodes and stale Filter modulation connections), preserve original external source/Envelope/Filter IDs, and copy per-node Filter configuration, positions, route-hint instances and per-node batch settings. Source colour and complete graph topology are retained.
- All seven root kinds and their script/data subtypes now support headless shallow duplication. UI remains disabled until both actions and proper tree placement/history are available. D3/D4 will supply reachable dependency closure and scripted remapping. Focused regressions include preflight source-asset mutation, Sample `.hm`/`.hm.json` persistence, independent tone/Filter settings, and graph fidelity.

**D3 — build generic deep-clone graph planner:** Per-runtime-type traversal policy; deterministic reachable set, global original-to-new-ID allocation and name map; repeated targets, diamonds, self-links, scripted cycles and overflow/stale preflight. Independent detached materialization with graph-wide reference remapping. Include Instrument source recursion and Synth sound-source/Envelope/Filter recursion.

**D4 — complete scripted deep handling:** Extend per-field reference extraction to every scripted runtime type, add Roslyn-aware `_O(id)` source rewriting plus re-analysis and refusal cases. Ensure script-text and typed references share the same global mapping. Tests with comments, strings, shadowed symbols, repeated references and cycles.

**D5 — integrate both actions in all six tree context menus:** Always show **Duplicate** for a concrete object, add **Duplicate (deep)** for Instruments, Synths and every scripted runtime type only. No duplicate action on folders or missing references. Put root next to selected tree occurrence, select it, support nested folders and deterministic dependency placement, and preserve keyboard focus without opening an editor.

**D6 — history, persistence and physical owner acceptance:** Guarded one-operation undo/redo for entire deep batches, song save/load in both formats including copied Samples, audio tests, all six pane GUI acceptance, TODO update and explicit approval before merge.

The user-requested traversal and menu rules override earlier references to a single generic "Duplicate Object" operation. Subsequent slices should follow this specification, not an unconditional clone-every-dependency traversal.
