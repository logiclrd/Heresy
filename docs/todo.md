# Heresy TODO

This file contains only explicitly specified work that remains open.

## Pattern entry and audition

- [ ] When pattern-entry note audition starts a new note on a tracker channel that
  is already auditioning a note, send Note Off to that channel's existing
  audition voice before starting the replacement note. Preview notes should not
  accumulate indefinitely on virtual channels; F8 must still terminate all
  remaining audition voices.
- [ ] Bind `Ctrl+Alt+Escape` to exit chord-entry mode and return the pattern
  editor to ordinary single-note entry.
- [ ] Make the chord-note indicator theme-aware. It currently renders black on
  black in dark mode; use the dialog/theme default foreground rather than a
  hard-coded note colour so it remains visible in every theme.

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
