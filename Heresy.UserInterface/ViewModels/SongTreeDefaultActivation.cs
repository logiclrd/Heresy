using System;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.ViewModels;

public enum SongTreeActivationKind
{
	None,
	Sequence,
	Pattern,
	Instrument,
	Envelope,
	Sample,
	FmSynth,
}

public static class SongTreeDefaultActivation
{
	public static SongTreeActivationKind Resolve(
		SongTreeSection section,
		SongTreeItemViewModel item)
	{
		ArgumentNullException.ThrowIfNull(item);

		if (item.IsFolder
			|| item.IsMissingReference)
		{
			return SongTreeActivationKind.None;
		}

		return (section, item.Kind) switch
		{
			(SongTreeSection.Sequences, SongObjectKind.Sequence) =>
				SongTreeActivationKind.Sequence,
			(SongTreeSection.Patterns, SongObjectKind.Pattern) =>
				SongTreeActivationKind.Pattern,
			(SongTreeSection.Instruments, SongObjectKind.Instrument) =>
				SongTreeActivationKind.Instrument,
			(SongTreeSection.Instruments, SongObjectKind.Envelope) =>
				SongTreeActivationKind.Envelope,
			(SongTreeSection.Samples, SongObjectKind.Sample) =>
				SongTreeActivationKind.Sample,
			(SongTreeSection.Samples, SongObjectKind.FmSynth) =>
				SongTreeActivationKind.FmSynth,
			_ =>
				SongTreeActivationKind.None,
		};
	}

	public static string GetMenuHeader(
		SongTreeActivationKind kind)
		=> kind switch
		{
			SongTreeActivationKind.Sequence =>
				"Edit Sequence...",
			SongTreeActivationKind.Pattern =>
				"Edit Pattern...",
			SongTreeActivationKind.Instrument =>
				"Edit Instrument...",
			SongTreeActivationKind.Envelope =>
				"Edit Envelope...",
			SongTreeActivationKind.Sample =>
				"Edit Sample...",
			SongTreeActivationKind.FmSynth =>
				"Edit FM Synth...",
			SongTreeActivationKind.None =>
				throw new ArgumentOutOfRangeException(
					nameof(kind)),
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(kind)),
		};
}
