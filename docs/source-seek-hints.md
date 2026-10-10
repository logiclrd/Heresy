# ReplayRequired native source seek hints

## Capability and correctness

`ISourceFrameSeekableSound.SeekCost` is an **advisory** classification:
`Direct` accesses source frames without reconstruction;
`ReplayRequired` may deterministically replay earlier private generator
state to address a requested source frame. Neither capability changes
the meaning of Oxx or Qxy and neither prohibits seeking. The offline
export engine remains sample-accurate; a seek with `ReplayRequired`
may be more expensive on realtime's single dedicated PCM worker.

The actual renderer, not the editor's static guess, is authoritative.
`PlaybackSession` reports a performance hint only after applying a
**positive native source-frame offset** to a bound voice whose sound
implements `ISourceFrameSeekableSound` with `ReplayRequired`.
It also reports a Qxy retrigger when the actual current bound voice
has that cost, because retriggering from source zero may reconstruct
state previously generated. O00 that resolves to a zero native offset,
nonseekable sources, and sounds reporting `Direct` do not generate
this hint. No change to the seek/retrigger execution algorithm is made.

`PreparedIncrementalPlaybackFactory` connects each root/private
`PlaybackSession.ReplayRequiredSeekObserved` to the same bounded
`SequencingDiagnosticLog` already used for sequencing warnings.
The implementation emits `HRSEQ005` for at most 32 individual
Oxx/Qxy observations per plan and one `HRSEQ006` suppression
message thereafter. Draining the log does not reset caps, and private
mixdown contexts share the root reporting sink. Existing realtime and
offline export paths drain and display these diagnostics on non-audio
threads. No app/UI callback runs on the SDL audio callback.

## Pattern editor analysis

`ReplayRequiredSeekWarnings.Analyze` traverses the displayed rows
of data Patterns/Sequences, tracking remembered Source selections
separately from the currently started logical note, and per-channel
low/high Oxx memory. It highlights Oxx **only on a note-start row
with a nonzero effective native offset**. SAx alone updates memory
and does not seek. It highlights Qxy when a retrigger countdown
is potentially active for the current voice. A direct Pattern/Sequence
invocation with `Mixdown: true` is statically known as
`ReplayRequired`; a non-mixdown flattened invocation is not
a single seekable sound, and ordinary Sample/FM sources are not
misrepresented as recursive mixdowns. An Instrument that might
select a recursively rendered Pattern/Sequence is marked as a
**conditional hint**, because pitch-dependent tone selection can
choose another branch.

Displayed Sequence orders retain independent occurrence identity,
including remembered Source across ordinary order transitions.
Script/missing entries and Bxx/Cxx flow invalidate certainty;
advisory text becomes conditional rather than claiming a guaranteed
expensive seek. Stored effects are untouched. Indicators coexist
with the existing flattened-source effect warnings; a clock glyph
marks a known performance hint and `?` marks conditional cases.
Tooltips explain that realtime reconstruction can be costly while
offline export is accurate.

## Optional suppression

**Options → Show expensive source seek hints** is a checked,
session-only preference. Unchecking it removes seek indicators and
tooltips from the current Pattern editor and suppresses *future*
HRSEQ005/HRSEQ006 display in the shared Runtime Diagnostics/Warnings
history (including export notifications). The underlying bounded
diagnostics still record and drain normally. Previously displayed
warnings are retained in history until the user clears them. This
is **presentation-only**: it never suppresses, shortens or changes
a supported Oxx/Qxy seek. Re-enabling the option immediately
refreshes the current Pattern editor.

Tests check direct versus replay-required bound sounds, zero-offset
behavior, exact source-frame offsets, Qxy callbacks, bounded diagnostic
delivery even after drains, private-child reporting, remembered Oxx
memory, ordinary/dynamic/Instrument editor classifications, and the
session preference.

The production `RecursiveNativeSeekBoundaryTests` additionally validate
actual tracker O01 against independently rendered frames **256–319** of
a private child Sequence (direct or Instrument-selected), native
O01/Q01 retrieval and replay across tick and row boundaries, O00 memory
through a new Sequence order invocation, seeking past a finite child's
logical end without contaminating a subsequent invocation, a private
mixdown nested within another private mixdown, and stereo PCM parity
of realtime and offline sources with deliberately different block
sizes. The `PlaybackVoice` uses the selected `SoundInvocation.Sound`,
so Instrument binding already preserves `ISourceFrameSeekableSound`
without special adapter forwarding. Rewinds keep replaying deterministic
child generators rather than retaining previously generated data.
