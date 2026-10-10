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

## User-facing configuration and snapshot boundaries

The desktop **Options → Audio Output** dialog edits the selected
sample rate, speaker layout (mono, stereo, 5.1, 7.1), ordered output
positions (X/Y/Z), positional importance, filter type and cutoff per
speaker. It may also reopen a render configuration with custom
speaker positions and edit that configuration directly. Its 5.1/7.1
output ordering is front-left, front-right, center, LFE, rear-left,
rear-right, then side-left/side-right for 7.1. An LFE output is an
ordinary speaker feed: **no bass-management crossover or implicit
low-frequency extraction** is inserted. A custom importance of zero
does not delete the speaker feed.

`AudioOutputSettings.Current` holds the immutable selected
`RenderConfiguration`, exchanged atomically on Apply. The application
constructs both `PlaybackRequestAudioSourceFactory` and
`OfflineSongRenderPlanFactory` with the same configuration provider.
Each factory captures the choice exactly once at **new render-plan
creation**; the configuration of an active playback source or export
is never mutated. Before publishing a new choice, MainWindow awaits
`StopAsync` to dispose the active SDL stream. The next realtime
request opens the proper channel-count/sample-rate stream. Offline
export captures the format synchronously before starting the
background file render, so changing UI preferences does not change
the format of an already running file.

Default output is 48 kHz stereo on first launch or if local settings
cannot be loaded. Settings apply to the **application**, not to the
song document. `AudioOutputPreference` now stores a version-1 JSON
snapshot under `LocalApplicationData/Heresy/audio-output.v1.json`.
At desktop startup `App` injects that preference into
`AudioOutputSettings` **before** realtime transport or offline export
factories are constructed. A subsequent successful dialog Apply
first stops the old SDL session, publishes the new immutable
configuration, then attempts to save it. Dialog Cancel, aborted
stop, and startup loading do not rewrite the preference.

The format preserves the sample rate and ordered 1/2/6/8 speaker
feeds, with each speaker's X/Y/Z position, positional importance,
filter type and active cutoff. Unknown versions, missing fields,
invalid layouts, nonfinite/out-of-range parameters and malformed
JSON are rejected as a **whole snapshot** (never partly restored).
The user-configurable sample rate is limited to 8–384 kHz and
active LowPass/HighPass cutoffs must lie strictly below Nyquist.
Filter fields with type None are ignored. Writes use a unique
temporary sibling and atomic replacement; missing/unreadable/
unwritable settings are nonfatal. A failed disk write leaves the
new in-memory selection available for the next render and does
not change previously captured playback/export configurations.

Tests cover mono/stereo/5.1/7.1 preset ordering, speaker positions
and filter selections, configuration snapshots across consecutive
realtime and offline requests, 5.1/7.1 renderer speaker isolation
with per-output filtering, and actual WAV header sample rate/channel
counts from two concurrent background exports using successive
configurations. `AudioOutputPreferenceTests` additionally exercise
persistence of every speaker field and ordering, first-run defaults,
invalid versions/layouts/rates/filters/cutoffs/nonfinite coordinates,
atomic temporary cleanup, unavailable storage, and immutable snapshot
isolation across preference changes.

## Still open

The engine renders the selected layout through realtime and export,
and the UI now persists the selected output across desktop launches.
Actual hardware-speaker mapping on different SDL devices and
encoder-specific 5.1/7.1 support still require device/integration
coverage. No automatic LFE bass management is implemented.
Those concerns, along with selectable WAV bit depths, remain separate
from the completed renderer, configuration and preference path in
[todo.md](todo.md).

## Realtime playback health

The same single PCM worker and bounded ring used by SDL continue to handle
synthesis and sequencing. `BufferedAudioOutputSource.UnderrunCount` is an
atomic count of short callback reads; those calls emit silence for missing
frames **without** advancing the musical generator. `RenderingFault` is
captured when the PCM worker stops after an exception. SDL also captures its
own callback/queueing failures in `SdlAudioOutputSession.Fault`.

The SDL output session implements the optional
`IAudioOutputUnderrunCounter` interface; the existing
`IAudioOutputSession.Fault` carries either kind of failure. The
`BackgroundPlaybackController` publishes immutable session identities
with each open/stop and exposes a read-only `GetAudioHealth()` snapshot.
Status can be sampled concurrently without taking locks in or invoking
callbacks from SDL or its PCM worker.

`SongPlaybackTransport` polls this health on the **transport timer thread**
and publishes `IPlaybackAudioHealthTransport.AudioHealthChanged` only on
session transitions, count changes or the first observation of a fault.
Faulty event subscribers are isolated. Finite pattern-position completion
no longer stops the timer entirely while the audio session is still active;
ad-hoc audition has the same monitoring. `LazySongPlaybackTransport` relays
subscriptions without forcing eager SDL initialization.

The Avalonia main window dispatches health notifications to its UI thread.
Its status-bar **Audio underruns** counter is completely invisible at zero,
becomes visible on the first underrun and resets for each session.
Recoverable underruns do not generate an unbounded diagnostic entry per
audio callback. Worker and SDL output faults are reported **once per
session**, in the existing bounded last-500-entry Runtime Diagnostics /
Warnings history and as a playback-failure status. Generation IDs reject
stale session updates after stop/replacement. The SDL callback still never
executes scripts, waits, or touches controls.

Regression coverage exercises empty-ring silence, count increments,
worker-exception retention, per-session observation/reset, duplicate-fault
suppression, observer isolation and subscriptions through lazy transport.
