using System;
using System.Numerics;

using Heresy.Render.Configuration;
using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class AudioOutputSettingsTests
{
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(6)]
	[TestCase(8)]
	public void SpeakerPresetsPreserveStableSpeakerOrderAndRequestedSampleRate(int channels)
	{
		RenderConfiguration preset = AudioOutputSettings.Preset(44100, channels);
		Assert.That(preset.SampleRate, Is.EqualTo(44100));
		Assert.That(preset.OutputChannelCount, Is.EqualTo(channels));
		for (int i = 0; i < channels; i++)
		{
			Assert.That(AudioOutputSettings.SpeakerName(channels, i),
				Is.Not.Empty);
			Assert.That(preset.OutputChannels[i].FilterType,
				Is.EqualTo(OutputFilterType.None));
			Assert.That(preset.OutputChannels[i].PositionalImportance,
				Is.EqualTo(1));
		}
		if (channels == 6 || channels == 8)
		{
			Assert.That(AudioOutputSettings.SpeakerName(channels, 2),
				Is.EqualTo("Center"));
			Assert.That(AudioOutputSettings.SpeakerName(channels, 3),
				Is.EqualTo("LFE"));
			Assert.That(AudioOutputSettings.SpeakerName(channels, 4),
				Is.EqualTo("Rear L"));
			Assert.That(AudioOutputSettings.SpeakerName(channels, 5),
				Is.EqualTo("Rear R"));
		}
	}

	[Test]
	public void SettingsSwapPreservesOldRenderSnapshotAndAllSpeakerParameters()
	{
		AudioOutputSettings settings = new();
		RenderConfiguration old = settings.Current;
		Assert.That(old.SampleRate, Is.EqualTo(48000));
		Assert.That(old.OutputChannelCount, Is.EqualTo(2));

		RenderConfiguration customized = new(96000,
		[
			new OutputChannelConfiguration(
				new Vector3(-0.75f, 0.25f, 1),
				positionalImportance: 3.0,
				filterType: OutputFilterType.HighPass,
				cutoffHz: 180),
			new OutputChannelConfiguration(
				new Vector3(0.75f, 0.25f, 1),
				positionalImportance: 0.5),
		]);
		settings.Set(customized);
		Assert.That(settings.Current, Is.SameAs(customized));
		Assert.That(settings.Current.OutputChannels[0].Position,
			Is.EqualTo(new Vector3(-0.75f, 0.25f, 1)));
		Assert.That(settings.Current.OutputChannels[0].FilterType,
			Is.EqualTo(OutputFilterType.HighPass));
		Assert.That(settings.Current.OutputChannels[0].CutoffHz,
			Is.EqualTo(180));
		Assert.That(old.SampleRate, Is.EqualTo(48000));
		Assert.That(old.OutputChannels[0].FilterType,
			Is.EqualTo(OutputFilterType.None),
			"Changing output preferences cannot mutate a previously started render.");
	}

	[Test]
	public void UnknownPresetIsRejectedWithoutMutatingCurrentSelection()
	{
		AudioOutputSettings settings = new();
		Assert.Throws<ArgumentOutOfRangeException>(
			() => AudioOutputSettings.Preset(48000, 3));
		Assert.That(settings.Current.OutputChannelCount, Is.EqualTo(2));
	}
}
