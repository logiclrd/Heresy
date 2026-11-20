# Same-song Duplicate Object — bounded implementation plan

**Status:** Slice D1 implemented on draft PR (not user-accepted; further slices required).
**Scope:** Every *object* in each of the six main-window song-tree sections, including the Samples/"Patches" section's Samples and Synths. No folder duplication in this feature. This is independent of the graph-aware cross-song **Import Synths** feature merged in PR #34.

## Contract

- **Definition vs reference:** Duplicate the selected *definition* with a newly allocated song ObjectId. Do not recursively duplicate everything that it references, and do not rewrite the original's existing users. Same-song references inside a copied Pattern, Sequence, Instrument or Synth retain the existing target ObjectIds. A copy may thus remain linked to shared Samples, Filters, Envelopes, Instruments, Patterns, Synths and other dependencies, as the original was.
- **Independent mutable authoring state:** Never reuse a mutable collection, Pattern cell/note/effect instance, Instrument tone specification/FilterInstance, Filter stage definition, FM graph node, editor node-position collection, or Sample's pending encoded byte container from the source object. Immutable value types, immutable Sample decoded PCM and referenced **other** song objects may be shared according to their explicit ownership contracts.
- **Names:** Suggest the next unused terminal numeric suffix among same-kind definitions, case-insensitively, matching the import naming policy. For example, duplicating "Pad 3" proposes "Pad 4", skipping occupied "Pad 4" and choosing "Pad 5". No uniqueness is imposed on manual renames.
- **Visual metadata:** Preserve source colours for sound-source definitions, plus authored graph editor positions/routing, Pattern dimensions/highlight rows, Envelope viewport and Filter stage allocator/generation history.
- **Placement:** On UI invocation, put the new object in the same tree folder as the selected occurrence (preferably immediately after it) and select the new placement. The tree is *organization*, not a dependency ownership graph. Handle another placement of the same referenced original unambiguously.
- **Safety:** Prepare the complete duplicate without touching the song or incrementing its ID allocator. Reject missing objects, unsupported runtime subtypes and unreadable/corrupt Sample assets before publication. Use an existing atomic whole-object commit with revision/fingerprint checks and a guarded undo receipt, no partial Sample or tree mutations. Later UI undo must not masquerade as the existing one-level **last Synth import** undo; either extend the history contract intentionally or offer a separate well-defined undo unit. Existing script references remain unchanged text, not searched-and-rewritten IDs.
- **Sample encoding:** A copied Sample must carry detached encoded bytes for saving in either .hm or .hm.json, not merely borrow the old asset path (or mutable on-disk bytes). Re-read/verify its encoded representation at acceptance to avoid stale external-file content. Reuse the existing immutable decoded PCM only after equality with the newly decoded encoded content has been established. Copy authored loops and channel positions.
- **Tests:** Headless per-runtime-type fidelity and independence, same-song reference identities, nested organization, no initial mutation, stale-preflight refusal, atomic rollback, undo/redo, save/reopen both song formats and audible preparation. Physical GUI tests in both light/dark themes where relevant.

## Slices

**D1 — detached models, name suggestions and atomic root-level commit foundation (draft PR):**
- Implemented for Data/Script Sequences, Data/Script Patterns, Node/Script Envelopes and Filter definitions.
- Data Pattern cells and note/effect records are newly constructed; they retain existing song reference IDs.
- Filter stage IDs, defaults, generations and next ID; Node Envelope sustain and viewport; Pattern source colour are preserved.
- A detached draft records expected next ID and full authoring snapshot. Existing `SongDocument.CommitImportedObjects` provides one atomic commit and guarded Undo/Redo receipt.
- D1 is **not exposed in the UI**. Initial programmatic commit places the new object in its section root; the final GUI must place it adjacent to the selected occurrence in its folder.

**D2 — complete remaining runtime types:** Sample, Instrument, Script Instrument, Synth, including own FilterInstance and graph-node copies, same-song references, detached Sample encoding and regression coverage.

**D3 — main-window UI:** expose **Duplicate Object** consistently across all six panes (context menu, perhaps Edit menu/keyboard if ergonomically appropriate), only on an existing nonmissing object. Create and select the sibling placement. Preserve focus without forcing editor activation. Add tests covering menu labels and every tree section.

**D4 — finish history/persistence and owner acceptance:** integrate whole-object undo/redo with a deliberately defined document-scoped UI history, test deep copies and Sample save/reload, update TODO, and obtain owner GUI approval before merging.

These are implementation boundaries rather than unchangeable product requirements; refine as tests and owner feedback warrant.
