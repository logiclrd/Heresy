# Synth import planning and dependency resolution

**Status:** Approved design; **not yet implemented**.  
**Scope:** The existing **Import Synths** workflow, including importing Synths back into their own saved song. Independent of the later **Duplicate Object** action for every object list.  
**Design recorded:** 12 November 2026.  
**Priority:** Park this while PR #12 (live Synth parameter audition) continues.

## 1. Goal and operating principle

Replace the existing source-Synth selection-and-copy workflow with a **graph-aware, interactive import plan**. Initially construct the best-effort plan for a **musically equivalent** Synth, copying its complete required dependency closure or reusing strict exact local matches. The user can intentionally change the result drastically: map a Source to a different local Sample, Instrument or Synth; choose a different Envelope or Filter; disconnect an input; omit a branch; or choose independent copies instead of sharing.

**Validity, not musical equivalence, is the hard constraint.** An intentionally musically different result is valid if Heresy's object, node, port, reference and cycle rules accept it. Do not silently force equivalence or prohibit legitimate, deliberate remappings. Show consequences in the tree and the continuously visible summary.

This import feature is independent of, and **does not complete**, the open general-purpose **Duplicate Object** TODO. There is no requirement to add a separate Duplicate command as part of this work.

## 2. Existing code and observed problem

Current code already has useful import primitives: source-song loading and enumeration, Synth cloning with node positions and connection-routing hints, fresh destination ObjectIds, recursive traversal of nested Synths / data Instruments / script Instruments / Samples, Envelope and Filter cloning, shared dependency maps, and staged Sample bytes.

However, the two existing importer paths disagree about supported nodes:

- **FmSynthDocumentEditor.ImportFromSong** supports a **Multi** node when the selected Synth contains no Source nodes.
- **FmSynthSourceImport.Visit** takes over if any selected Synth contains a **Source** node, recursively visits source dependencies, but its node-type switch does **not** admit **FmMultiNode**. A Synth containing both a Source and Multi therefore has a plausible unsupported-node failure.
- The main-window Import Synths handler catches exceptions into the status area and subsequently overwrites them with **"No synths were imported."** This can hide the actual error.

The user's first attempt to import a saved Synth back into the current song produced zero imports both before and after renaming the local Synth. The precise saved-song input was not reproduced under test, so the Multi case is a **strong code-level diagnosis, not a proven reproduction**.

**Do not first implement a throwaway standalone Multi-node patch** unless needed for some independent reason: incorporate its coverage and fix into the replacement importer. Do not turn a test that merely asserts today's refusal into an approved invariant.

## 3. The data model: graph, plan, and tree projection

Maintain three distinct layers:

1. **Source/dependency graph:** immutable discovered object and node identities, incoming/outgoing uses, port identities and metadata, including dependencies of nested objects.
2. **Editable import plan:** the user's per-object and per-use decisions, any explicit overrides, suggested target names, proposed old-to-new mapping, import exclusions, and stable presentation-ownership identities. A single authoritative plan determines what the operation would do.
3. **TreeDataGrid projection:** a navigable, expandable **tree view of the dependency graph**, not the graph's underlying storage representation. The tree may display one entity more than once, but those are presentation occurrences, not duplicated imported entities.

A source-object identity is at least (**source document identity**, **source ObjectId**). Never conflate same numeric ObjectIds from different input files. Internal graph-node identity is (**source Synth identity**, **graph node ID**); internal node IDs can normally be preserved in the new Synth because the **Synth's song ObjectId** changes. Stable tree **occurrence identity** includes the owning use/connection and semantic input port, rather than being just the entity's ID or its current visual row number.

Preserve graph node positions, authored parameters, oscillator phases' *definitions* (not runtime state), Source frequency/modulation settings, Multi distribution and related values, output selection, connection-routing hints, source colour and other persisted editor metadata. Runtime PCM/filter histories are never import dependencies.

### 3.1 Roots and traversal

For each selected Synth, show it as a top-level entry. Under it, roots are internal nodes **not used as input to any other graph node**, with the designated output/root first. Traverse **from consuming node to its input nodes** and then any referenced external objects, and recursively through nested Synths and Instrument tone specifications. Show the **semantic input-port role** (Audio, CutoffHz, etc.), not just positional child numbering.

Disconnected authoring fragments which already existed in the source are part of the source Synth and should not disappear merely because they do not reach the Output. They may appear as additional roots.

Each row provides a readable **planned action** and an appropriate editor: target lookup, import-as-name, reuse exact match, manual map, None/disconnect where legal, or exclude an object/branch where valid. A row's target action depends on whether it represents an internal node, a connection/use, or an external object definition. Do not expose invalid actions just to make all row menus identical.

### 3.2 Canonical presentation and aliases

An underlying node or external object has **one canonical presentation occurrence** that owns its import decision, and other displayed occurrences are **alias/link rows**. Secondary occurrences are slightly gray and labelled like **"another use of #N"** / **"linked to [object]"**. Double-click or a navigate action expands and scrolls to the canonical occurrence; show a use/reference count where helpful.

Canonical ownership is a *stable state of the plan*, **not recalculated by first depth-first appearance on every refresh**.

If a link is removed, or mapped to a different object, such that the use hosting the canonical occurrence is no longer active:

1. If another **active use** of that same entity exists, **promote one of those uses to canonical**. Demote the former occurrence to the gray linked/inactive style and make it navigate to the new canonical row.
2. **Do not automatically transfer ownership back** if the original link is restored later. Rehome only when the current canonical owner actually becomes invalid/inactive. This minimizes visual churn.
3. Apply the same rule to internal graph nodes, shared Source definitions, shared Envelopes/Filters, and shared dependencies at any nesting level.
4. If there is no active use, retain a stable disconnected-root presentation when the entity is intentionally preserved in the import; otherwise show the now-inactive branch as excluded/not imported. No orphaned alias may point to a missing canonical row.

Recalculation should preserve expanded rows, selection, scrolling and the user's focused edit as far as possible. A canonical move should preserve entity identity and navigate/selection semantics rather than looking like object destruction and recreation.

## 4. What a decision means

### 4.1 Selected Synth and naming

**Every explicitly selected root Synth always imports as a new object with a fresh destination ObjectId**, even if the source file is the current song, the source ID is already present, the contents are identical, or a local object has the same name. **Never treat matching IDs or names as reasons to skip it.**

Names need not be unique for data correctness. Nevertheless, the **default generated name avoids collisions for usability**, checking the relevant destination list/section **and other reserved names in the same proposed batch**. For example, source **Trumpet** against local **Trumpet** gets **Trumpet 2**, and then **Trumpet 3** if needed; reuse the first available suffix. Apply this to newly copied dependent objects as appropriate, not just explicitly selected roots.

Show editable proposed names in the plan. A user may explicitly choose **Trumpet** even if another object named **Trumpet** exists; do not refuse, silently rewrite or require an additional confirmation. Once a user edits or accepts a proposed name, keep it stable across unrelated plan recalculations. Picker choices must be disambiguated by **type and ObjectId** (and origin/song where relevant), since duplicate names are legitimate.

### 4.2 External object references

For a referenced object (Sample, data/script Instrument, nested Synth, Filter definition, Node/Script Envelope), support appropriate choices:

- **Import copy:** allocate a new destination ID and clone appropriate persisted content and reachable dependencies.
- **Reuse exact local match:** use a strongly validated equivalent destination object, without copying it.
- **Map to existing:** let the user deliberately choose a compatible destination object **even if it is musically different**. In particular, a Source node can be mapped to any currently supported invocable sound-source type, not merely another Sample.
- **None / disconnect:** where the actual Core node/port or tone schema permits an unassigned reference, allow it and visibly show any resulting silence or changed structure.
- **Exclude branch / node:** where removal can produce a valid graph. Treat disconnecting a connection separately from deleting the referenced node; a node used elsewhere may remain necessary.

Decisions for a **definition** apply to all its uses in the batch by default. A user can also deliberately override a **specific use** (for example, one Source node maps to a different Sample without remapping every use of the original). Represent explicit per-use overrides separately; don't accidentally split or duplicate every other use. Two or more selected Synths that share a source object share one planned copied dependency by default.

### 4.3 Strict automatic matches, highlighting, and manual provenance

The planner examines existing local definitions and **automatically reuses strict exact matches**, notably reusable Filter definitions and Envelopes. This is the default heuristic, not mandatory or inferred from the name alone. If no suitable exact match is found, **import a fresh copy** by default. Offer manual mapping even to intentionally non-equivalent objects where supported.

Rows for **automatically established exact-match reuse** receive an **orange highlight**, with an icon/label such as **"Automatic exact match — reuse local #42"** and an immediately accessible **"Import separate copy"** action. The highlight signifies *automatic sharing*, **not an error**; never rely solely on color. A reused definition becomes jointly editable across any Synths using it; importing a separate copy preserves independent edits.

**Highlight provenance is sticky and one-way on manual intervention.** Once the user changes an automatically matched row away from the exact-match choice, the orange highlight disappears. If the user later **explicitly chooses that same exact match again**, it **stays unhighlighted**, because the mapping is now a **manual choice**, no longer an automatic recommendation. Further recalculations must not reassign automatic provenance or overwrite explicit decisions. Only an actual **Reset to Defaults** may regenerate automatic matches and their orange provenance.

Suggested model metadata: automatic heuristic vs explicit user choice, plus selected action/target. Do not derive "automatic" from the fact that the chosen target happens to be equivalent.

### 4.4 Matching quality

- **Names are not semantic equality**, and shared numeric IDs between the saved source and the open target are not proof that content is unchanged. The live song may have unsaved modifications.
- **Node Envelopes:** compare all audible curve nodes, timestamps, values and sustain boundaries/modes; decide explicitly whether editor-only display range belongs to strict *object* equality (for fully faithful copies, default to including it).
- **Script Envelopes:** compare expression and release-duration semantics (and any persisted relevant properties).
- **Filter definitions:** compare ordered stages, stage kinds, parameter defaults and port semantics. Stable **stage IDs and generation/identity history** matter to Filter-instance overrides. Prefer strict identity-compatible reuse; if an apparently equivalent definition uses different stage IDs, allow reuse only with an explicit, verified remapping of Filter instances and their per-stage overrides. Do not silently drop values or call two configurations equivalent merely because their response initially sounds similar.
- **Samples:** same name/path is not proof of equality. Content identity needs strong encoded/decoded data comparison plus loop, channel positions and playback-relevant metadata. Prefer copying unless genuinely verified or manually mapped.
- **Instruments/nested Synths:** content equivalence is more complex because they have recursive dependencies, scripts and graphs. Default to copying unless a strong compatible match can be established; user may explicitly reuse any schema-compatible target.

All matches must respect object type when required, and any eligible substitution must be validated after effective rewiring.

## 5. Filtering, ports, and graph changes

When a user maps a Filter definition to a **different Filter** with different available inputs:

1. Match ports by **stable semantic identity/role and compatible parameter type**, not merely their numerical location.
2. Preserve compatible connections and instance overrides where a validated translation exists.
3. Disable or disconnect unmatched ports; in the TreeDataGrid, make the corresponding branch **gray/inactive** and replace its action summary with a clear reason such as **"Not imported: destination Filter has no matching Q input"**.
4. Recompute whether each disconnected child node/dependency has **other live uses**. If so, it remains included and, when necessary, its canonical tree ownership moves to one of those uses under the sticky rule above.
5. If a branch has no remaining use, represent the consequence honestly as **not imported** unless the user explicitly preserves it as a disconnected authoring fragment where the graph allows that. Do not silently remove nodes that were *already* intentionally disconnected in the source.
6. Restoration of a compatible port/connection may reactivate a branch but does **not** steal canonical ownership back from a valid promoted occurrence.

The same approach applies to all rewired or removed graph connections. Disconnecting a **use** is not necessarily deleting the entity, and a dependency reused elsewhere is not recopied just because a branch changes.

Do not silently reinterpret incompatible Filter stages, drop overrides, rewire invalid input types, or leave dangling references. A deliberate sound change may be accepted; an invalid graph may not.

## 6. Live plan recomputation

Every user change updates the **effective import plan** and the TreeDataGrid/summary immediately. This is a pure planning operation: do **not** create actual destination song objects, copy assets into the live document, or increment revisions before final acceptance.

Recalculate:

- Required roots, active connections, reachable dependencies and explicitly preserved disconnected nodes.
- Shared and split mappings, active/inactive aliases, canonical ownership only when necessary.
- Automatic matches **only for untouched decisions**; preserve manual mapping, manual copy, name choices and manual provenance.
- The resulting new/reused/remapped object counts, disconnections, exclusions, and warnings.
- **Internal consistency** of the proposed imported subgraph and its own assignments: valid node types, required ports, non-dangling planned references, correct types, valid local wiring, no internal source cycles, usable planned assets, and coherent unique destination IDs. This internal check may use known schema constraints without exhaustively validating the rest of the destination song.

**No normal modal confirmation after viewing the plan.** Allow user edits to produce a musically different result; only invalid decisions should block Import.

Keep a **Reset to Defaults** action for the plan. Undo Last Plan Change is a useful optional dialog convenience, not a prerequisite for the import engine.

## 7. Summary and the two-stage validation boundary

The continuously visible summary sits **directly below the TreeDataGrid**, updating in realtime. At minimum report:

- New objects to create, including names and object types when expanded.
- Existing objects automatically or manually reused.
- Explicitly remapped uses and deliberate non-equivalent substitutions.
- Disconnected input ports and excluded branches/objects.
- **Internal** validity errors (versus allowed warnings for behavioral changes).

The user has already seen these consequences before accepting, so **do not add another confirmation step solely for the summary**.

**Two-stage validity is an explicit UI policy:**

**Stage A — interactive internal validation:** While the user edits, validate the **proposed imported plan itself**, not the entire destination song. Show internal failures under the grid and **disable Import** if internally inconsistent. Enable Import as soon as the plan is internally consistent, even when the chosen references are intentionally musically different.

**Stage B — explicit broader destination validation on Import click:** Before mutating the song, validate the **combined result against the existing destination song** (including indirect local Source/Instrument/Synth references and cycles, tombstones/ID rules, compatible object relationships, assets and any other song-wide invariants). This may fail despite Stage A passing. In that case, show a **message box describing the concrete destination-wide conflict**, abort **before any modification**, dismiss the message box back into the **same import dialog**, and preserve every user decision, expansion and scroll position for correction. Do not replace the internal summary with a permanent song-wide error or require global validation on every keystroke.

Revalidate the effective plan and destination at click time, as the destination may have changed since planning began. Only commit when both stages pass. If an unexpected failure occurs during actual commit, the destination must remain unchanged; use a staged transaction / rollback equivalent, including revision and persisted-asset ownership. One accepted batch should be an undoable unit where supported by the workspace's undo architecture.

## 8. Sample asset rules

Heresy keeps decoded Sample PCM in memory for playback, but persists encoded data differently by song format:

- **.hm**: a ZIP-backed song package containing its encoded Sample assets.
- **.hm.json**: JSON song metadata with external encoded-Sample paths, typically relative. Opening loads/validates/decodes assets into the live document.

Therefore a copied Sample must own independently persistable **encoded bytes**, not retain a path into the source archive or require the source song to remain available. Preserve Sample loop settings, source-channel positions, source colour and other authoring metadata. Existing Source/Filter DSP histories are not copied. Reuse of an existing local Sample needs no new encoded asset.

Report missing/unreadable/corrupt external encodings accurately and offer appropriate asset recovery or dependency remapping where supported; don't silently clear the node or falsely imply that live decoded PCM removes the need for encoded data on save. The import plan may permit an explicitly changed mapping or disconnected reference that removes the now-unneeded asset dependency.

## 9. Source selection, user interaction and errors

- Keep the existing choose-source-song(s) and choose-Synths selection. Build **one effective plan per selected batch** so dependencies shared between selected roots remain shared and naming reservations are consistent.
- The tree should show all relevant root Synths and internal graph nodes, with referenced Source/Instrument/Sample/Filter/Envelope objects as children. Every occurrence shows its role; aliases navigate to the canonical row.
- For each object-reference row, expose a suitable destination chooser with existing local compatible objects identified by type, name and ObjectId. Where an object can be assigned **None**, say explicitly whether doing so silences a sound or discards a connection.
- Default imported names avoid conflicts, but arbitrary duplicate names are valid.
- Use **orange only for active automatically established exact-match rows**; the highlight is removed permanently for that decision on manual intervention, even if the same destination is selected again.
- Keep structural errors and permitted behavioral warnings distinct in row/status displays. Missing data must not disappear behind generic "zero imported" messages.
- On success, report created Synth(s), counts of copied/reused objects, and select/open the first requested Synth, without confusing newly imported child Synths with additional user-selected roots.
- On cancel or failed destination-wide validation, leave the active song untouched. On failed destination-wide validation, return to the same plan dialog, not the source-file picker.

## 10. Architectural components

A practical implementation split:

1. **Importer graph discovery** (read-only), including complete node-type coverage and Source/Instrument/Filter/Envelope dependencies.
2. **ImportPlan domain object** with stable entity/use identities, mapping provenance, names, per-use overrides, active connections, optional inclusions and sticky canonical row identity.
3. **Recalculation engine** producing an effective plan and internal validation without modifying the SongDocument.
4. **Avalonia TreeDataGrid presentation**, context-sensitive row editors, alias navigation, orange automatic reuse indicators and persistent below-grid summary.
5. **Destination-wide validator** invoked when Import is clicked, with a recoverable message box on failure.
6. **Staged atomic commit** creating new IDs and persistent encoded Sample assets, remapping definitions/Filter instances and retaining valid object graphs without partial mutation.

Share one authoritative list/visitor for supported FM node types across all cases. The old simple-Synth and recursive-Source importers must no longer disagree on Multi support.

## 11. Regression and acceptance plan

Use invariant-based red-first tests for missing behavior rather than preserving current defects as expected results. At minimum cover:

1. **Same-file import** of a Sample → Filter + Oscillator → Add → Multi → outer Operator Synth. Import again into a song with the same root ID and name; expect a **new Synth**, suggested **"Name 2"**, preserved graph/audio/editor metadata and correct live reference mapping. This is the user's reported failure scenario.
2. Import again with the **exact same name chosen explicitly**; allow multiple visually identical names but distinct IDs.
3. Default exact-match reuse of an Envelope and a Filter with **orange automatic provenance**. Choose **Import separate copy**; highlight disappears. Manually reselect the *original exact local match*; it remains **unhighlighted**. Reset to Defaults may regenerate the highlight.
4. Shared dependencies across multiple Source nodes and selected Synths: one copied/reused entity by default, canonical row plus navigating aliases; per-use override changes only the targeted use.
5. **Sticky rehoming:** remove the canonical connection while another active use remains, promote that active use, demote original to alias; restore the original connection and verify **ownership does not move back**. Repeat with shared external object dependencies.
6. Filter swap with **fewer/differently typed ports**: retain matching wires, visibly mark incompatible ports inactive/not imported, preserve nodes still needed by other consumers, rehome canonical rows, and preserve already disconnected authoring fragments.
7. Default names **Trumpet, Trumpet 2, Trumpet 3** with collision detection against existing entries and other planned imports, plus an allowed explicit duplicate.
8. Remap Source from Sample to another valid sound-source category, remap Envelope, choose None where valid, and prove intentional musical divergence does not itself disable Import.
9. A **self-contained internally invalid plan** disables Import and shows the actual offending row/condition below the tree, without mutating the destination.
10. An **internally valid plan that creates a destination-wide Source cycle** leaves Import enabled initially; clicking Import shows a concrete conflict message and returns to the **same preserved dialog** without changing destination objects, assets, revisions, selection or plan edits.
11. Nested Synths, data/script Instruments, shared Node/Script Envelopes, stage-identity-sensitive Filter overrides, and Sample encodings from both **.hm** and **.hm.json**, including asset failures/recovery.
12. Cancellation, multiple file selection, selected roots vs auto-included dependent Synths, no duplicate child copies, correct tree metadata and object IDs after save/reload, and full rollback on commit failure.
13. Import of an already-disconnected node/branch, deliberately excluded branch, alias navigation and focus/scroll stability across repeated recalculation.
14. No runtime audio-synthesis modifications required for a purely authoring import; verify imported sounds still prepare, preview and export correctly.

## 12. Explicit non-goals and unresolved implementation choices

- **Not** the general main-window **Duplicate Object** feature.
- **Not** a forced exact-copy-only importer; musical divergence by explicit choice is allowed.
- **Not** unconditionally automatic reuse of Samples, Instruments or nested Synths based on names.
- **Not** per-keystroke validation of the whole destination song.
- **Not** extra confirmation dialogs for a summary already visible and accepted.
- **Not** a standalone patch for old importer Multi refusal ahead of replacing its architecture.

Implementation details still to choose: TreeDataGrid row-model APIs, convenient use-level override UX, precise strict-equivalence comparison for each definition family (especially Filter stage-generation translation), how optional disconnected-node inclusion is presented, transactional document/undo integration, and the detailed Sample asset-recovery control. Choose these within the principles and acceptance cases above, documenting tradeoffs when implemented.

**Scheduling:** This design is locked in on main for later work. Resume PR #12's all-nonstructural Synth live-parameter acceptance before starting this larger import rewrite.
