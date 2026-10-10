namespace Heresy.Render.File;

/// <summary>Integer PCM precision; 8-bit WAV uses unsigned bytes,
/// while 16/24/32-bit WAV use signed little-endian samples.</summary>
public enum WavePcmBitDepth
{
	Pcm8 = 8,
	Pcm16 = 16,
	Pcm24 = 24,
	Pcm32 = 32,
}
