using System;

namespace Heresy.Render.Sounds;

/// <summary>Optional event-deadline playhead capability, independent of
/// legacy additive onset offsets and note lifetime clocks. Relative seeks
/// use signed deltas from the current unwrapped source coordinate.</summary>
public interface IPlaybackSeekableSound
{
    void SeekTime(RenderContext context, SoundState state, long invocationFrame,
        TimeSpan time, bool relative);
    void SeekFrame(RenderContext context, SoundState state, long invocationFrame,
        long frame, bool relative);
}
