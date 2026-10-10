# Final speaker-output processing

## Implemented render path

The output layout is defined by `RenderConfiguration` and its ordered
`OutputChannelConfiguration` entries. A playback channel is a logical
voice host; the output channels are **speaker feeds**, and are not
one-to-one with tracker channels.

The master `PlaybackSession` mixes all physical, scoped virtual,
targeted virtual and NNA-displaced voices, their anti-click tails,
and applies global volume. **After the full mix**, it filters each
interleaved output-speaker sample independently using the configured
`OutputFilterType`:

- `None`: unchanged PCM.
- `LowPass`: a one-pole low-pass with the configured cutoff.
- `HighPass`: original speaker input minus the corresponding
  one-pole low-pass component.

The discrete coefficient is
`alpha = 1 - exp(-2*pi*cutoffHz/sampleRate)`.
Each speaker owns one low-pass state carried across render blocks,
including note transitions, silence, and replayed NNA voices. The
filter is **post-mix**, never part of a sample's per-voice tracker
resonant filter. Configurations with every filter set to `None`
do not allocate or process an output-filter bank.

Private recursive Pattern/Sequence mixdowns still emit the configured
number and order of speaker feeds, but their private
`PlaybackSession` instances use `applyFinalSpeakerFilters: false`.
Only the *outermost* output session applies the final filter; this
prevents nested Instrument-selected or direct mixdowns from being
filtered twice. Reconstructing a private mixdown for source seeking
does not reset the master filter's continuous state.

## End-of-input and deterministic export

An impulse response can continue after the last note ends. When
input has ended, `PlaybackSession.IsQuiescent` therefore also checks
the speaker-filter tail. Silent input eventually brings the one-pole
state to exact zero when its decaying magnitude falls below
`1e-7`; **nonzero input is never threshold-clamped**, so even very
low cutoff frequencies can accumulate correctly. The offline tail
renderer already drains until quiescence and trims trailing zero
frames; filter decay is included in its existing bounded-tail
contract.

Regression tests exercise asymmetric stereo LowPass/HighPass impulse
responses, independent speaker histories, unequal PCM block sizes,
a private recursive Pattern selected as a sound, the None bypass,
and filter state across the offline tail boundary.

## Still open

The engine accepts explicit output speaker layouts and sample rates,
but the application does not yet expose complete configurable
5.1/7.1 layouts, speaker placement, positional importance, per-speaker
cutoff or sample rate to users. End-to-end configurable realtime and
file-export speaker ordering and complex layouts also need broader
coverage. Those tasks remain in [todo.md](todo.md).
