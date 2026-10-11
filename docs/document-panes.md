# Document panes and version-1 tree compatibility

## Current layout

The desktop document view projects six fixed `SongTreeSection` roots:

| Position | Section label | Core section enum | Accepted object kinds |
|---|---|---|---|
| Top-left (2 columns) | Sequences | Sequences | Sequences |
| Top-right (2 columns) | Patterns | Patterns | Patterns |
| Bottom 1 | Patches | Samples | PCM Samples and FM Synths |
| Bottom 2 | Filters | Filters | Reusable Filter definitions |
| Bottom 3 | Envelopes | Envelopes | Envelopes |
| Bottom 4 | Instruments | Instruments | Instruments |

The single Avalonia grid has **four equally sized star columns**. Both top panes
span two columns; each bottom pane spans one. All panes retain separate tree
projections, section-local folder organization, drag/move validation and
rename/delete actions. Headers use wrapping actions to avoid clipping in
quarter-width panes. The `Samples` enum retains its historical name while
the UI calls that pane **Patches**.

`FilterDefinition` is a `SongObject` with `SongObjectKind.Filter`. It is
now admitted by `SongDocument.Add` and placed directly under the Filters
root; Filter folders can be reorganized within their section, but no objects or
folders can move between semantic roots. Filter definitions now serialize
and restore in both song formats, clone through song snapshots and can be
imported with fresh IDs using `FilterDocumentImport`. **Filter creation and
editing are not yet exposed in the UI** until the standalone definition
editor exists. Existing song types retain their current activation and
creation actions.

## Backward-compatible loading

All three layouts retain the pre-release file-format `version: 1`.
`SongDocument.RestoreTree` validates fixed section names and exact order:

- **Four-root legacy:** Sequences, Patterns, Instruments, Samples. Migrate
  as before by extracting Envelope placements (including tombstones) from
  Instruments into a new Envelopes root, mirroring only needed ancestor
  folders; convert Samples to Patches. The original mixed Instrument folders
  keep their remaining contents.
- **Five-root legacy:** Sequences, Patterns, Patches, Envelopes, Instruments.
  Preserve all existing subtrees, folders and placements unchanged. Insert
  one empty Filters root between Patches and Envelopes.
- **Six-root current:** Sequences, Patterns, Patches, Filters, Envelopes,
  Instruments. Load directly without migration.

Every in-memory song, regardless of its historical root count, therefore
exposes the exact six-root order. Subsequent saves emit six roots. The
upgrade never allocates object IDs, changes audio/document revisions, rewrites
source references or alters envelope definitions. Corrupt four-/five-/six-root
names and orders are rejected rather than guessed. JSON and packaged `.hm`
use the same loader and retain their other established compatibility rules.
Tombstone kind `filter` is recognized so missing Filter references can be
represented in the organizational tree.

Tests: `SongTreeSectionTests`, `FilterTreeMigrationTests`,
`SongDocumentJsonTests`, `DocumentPaneLayoutTests`,
`FilterPaneTreeTests`, `SongTreeDefaultActivationTests`,
`EnvelopeDocumentEditorTests`, and `SongTreeEditorTests`.

## Related completed editors

The graphical ADSR editor and its FM Envelope-node inspector integration
are implemented; see [envelope-graph.md](envelope-graph.md).
The implicitly managed nine-column Instrument tone grid is implemented;
see [instrument-tone-grid-model.md](instrument-tone-grid-model.md).
Neither belongs to the current [TODO list](todo.md).
