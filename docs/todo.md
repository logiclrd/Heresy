# Heresy TODO

This file contains only explicitly specified work that remains open.

## FM synth graph editor

- [ ] Add an FM-synth audition area containing the text `Test`. While keyboard
  focus is in that area, tracker piano keys (`Z S X D C ...`) should audition
  the corresponding notes, and `*` / `/` should change the audition octave
  exactly as they do in the pattern editor.
- [ ] Make node-parameter editing commit immediately when an input loses focus or
  when Enter is pressed. Escape in an input should restore its last committed
  value. Remove the Apply button; there should be no separate apply step.
- [ ] Make connection routing editable directly on the graph. Clicking anywhere
  along a connection and dragging should create a waypoint at that location,
  inserted into the existing waypoint sequence according to the segment that was
  clicked. Connection hit-testing should use a 3-pixel tolerance on either side
  of the rendered line. Double-clicking an existing waypoint should delete it.
  Once this interaction exists, remove the raw waypoint-data editing UI.

Completed implementation history is preserved in Git, while stable architectural
and behavioral rules belong in the focused documentation and README rather than
remaining as checked-off TODO entries.
