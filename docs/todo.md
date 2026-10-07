# Heresy TODO

This file contains only explicitly specified work that remains open.

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
