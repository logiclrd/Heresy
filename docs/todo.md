# Heresy TODO

This file contains only explicitly specified work that remains open.

## Document lifecycle and project view

- [ ] Prompt to save when closing the application while the current document is
  dirty. Use the standard Yes/No/Cancel flow: Yes saves and then exits, No exits
  without saving, and Cancel aborts the close.
- [ ] Double-clicking an editable item in the main project view should open its
  corresponding editor. The matching context-menu action should be bold to
  communicate that it is the default activation action.

## Offline rendering and export

- [ ] Add a streaming FLAC sink to `Heresy.Render.File` and make FLAC the
  default lossless export format. Do not implement this by buffering the entire
  rendered song in memory merely to call a whole-buffer codec facade.
- [ ] Add MP3 export on the same file-sink architecture, likewise avoiding
  whole-song PCM/encoded buffering. WAV remains 16-bit PCM by default.
- [ ] Expose offline rendering in the desktop UI with an Export/Render command
  that compiles an immutable song snapshot through the existing playback
  composition layer, chooses the sink from the destination format, and reports
  deterministic-tail failures clearly.

Completed implementation history is preserved in Git, while stable architectural
and behavioral rules belong in the focused documentation and README rather than
remaining as checked-off TODO entries.
