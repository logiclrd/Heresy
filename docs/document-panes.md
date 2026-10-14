# Document panes and version-1 tree compatibility

## Current layout

The desktop document view projects six fixed `SongTreeSection` roots:

| Position | Section label | Core section enum | Accepted object kinds |
|---|---|---|---|
| Top-left (2 columns) | Sequences | Sequences | Sequences |
| Top-right (2 columns) | Patterns | Patterns | Patterns |
| Bottom 1 | Patches | Samples | PCM Samples and Synths |
| Bottom 2 | Filters | Filters | Reusable Filter definitions |
| Bottom 3 | Envelopes | Envelopes | Envelopes |
| Bottom 4 | Instruments | Instruments | Instruments |

The single Avalonia grid has **four equally sized star columns**. Both top panes
span two columns; each bottom pane spans one. All panes retain separate tree
projections, section-local folder organization, drag/move validation and
rename/delete actions. Headers use wrapping actions to avoid clipping in
quarter-width panes. The `Samples` enum retains its historical name while
the UI calls that pane **Patches**. Synth-specific UI controls use **Synth**/**Synths** without the FM qualifier (`+ Synth`, `+ Import Synths`, and `Edit Synth...`). The implementation still uses `FmSynthDefinition`, `SongObjectKind.FmSynth` and the same v1 persistence representation; this is a presentation-only rename.

`FilterDefinition` is a `SongObject` with `SongObjectKind.Filter`. It is
now admitted by `SongDocument.Add` and placed directly under the Filters
root; Filter folders can be reorganized within their section, but no objects or
folders can move between semantic roots. Filter definitions now serialize
and restore in both song formats, clone through song snapshots and can be
imported with fresh IDs using `FilterDocumentImport`. Filter creation and editing are now available through the **+ Filter**
button and the **Edit Filter...** tree activation, with stage ordering,
rename/type selection and parameter defaults in the standalone editor.
Existing song types retain their current activation and creation actions.

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

## Related current editors

The standalone Envelope editor and FM Envelope-node inspector share an
interactive **node-envelope graph**; see [envelope graph](envelope-graph.md).
The implicitly managed **eleven-column Instrument tone grid** includes
per-tone NNA and Filter settings; see
[instrument tone grid](instrument-tone-grid-model.md).
Physical interaction and audible release acceptance remains separately
tracked in [open work](todo.md).
