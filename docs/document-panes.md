# Document panes and version-1 tree compatibility

## Current layout

The desktop document view projects five fixed `SongTreeSection` roots:

| Position | Section label | Core section enum | Accepted object kinds |
|---|---|---|---|
| Top-left | Sequences | Sequences | Sequences |
| Top-right | Patterns | Patterns | Patterns |
| Bottom-left | Patches | Samples | PCM Samples and FM Synths |
| Bottom-center | Envelopes | Envelopes | Envelopes |
| Bottom-right | Instruments | Instruments | Instruments |

The grid has six equal star columns. The two top panes span three
columns each; the three bottom panes span two each. Each pane has its
own tree projection, folder creation, and applicable object actions.
Header actions wrap to fit narrow panes. General sound-generator
containers use the term **Patches**; operations specific to sampled
PCM deliberately retain **Sample** (import audio, sample looping and
waveforms). The Core `Samples` enum value remains unchanged.

Creating an Envelope via `EnvelopeDocumentEditor` places it under
`SongTreeSection.Envelopes` automatically. The Envelopes pane
supplies + Envelope and activates the standard `EnvelopeEditorControl`
on double-click or context menu. Instruments contain Instrument
objects, and Patches contain both FM synths and PCM Samples.
`SongTreeEditor` continues enforcing section-local organizational
moves only; those moves change document revision, not audio revision.

## Backward-compatible loading

The pre-release file format intentionally remains `version: 1`.
Older documents used **four** fixed top-level folders in order:
Sequences, Patterns, Instruments, Samples. After all song objects,
tombstones and references have been restored, `SongDocument.RestoreTree`
recognizes that exact older structure. It migrates in memory:

1. Sequences and Patterns retain their subtree contents.
2. The original Samples subtree becomes **Patches**.
3. All Envelope placements, including Envelope tombstones, are removed
   from the old Instruments subtree and transferred to the new
   Envelopes root. Nested folders containing Envelope placements are
   mirrored, preserving their names and relative hierarchy. Mixed
   Instrument/Envelope folders remain in Instruments with their
   non-envelope children.
4. The five-root order is Sequences, Patterns, Patches, Envelopes,
   Instruments. New saves use this order.

The migration does **not** allocate new object IDs, rewrite musical
references, create audio effects, or advance either document revision
or audio revision. Unrecognized four-root structures are rejected
instead of being guessed into a different layout. JSON and packaged
`.hm` documents share this loader.

Tests: `SongTreeSectionTests`, `SongDocumentJsonTests`,
`DocumentPaneLayoutTests`, `SongTreeDefaultActivationTests`,
`EnvelopeDocumentEditorTests` and `SongTreeEditorTests`.

## Follow-on tasks

The graphical ADSR editing control and its embedded FM envelope
inspector remain separate unfinished work, as does the new
implicitly-managed Instrument tone-table grid. They are recorded in
[ui-editor-redesign.md](ui-editor-redesign.md) and [todo.md](todo.md).
