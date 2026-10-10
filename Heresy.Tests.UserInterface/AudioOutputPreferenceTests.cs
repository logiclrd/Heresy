using System;
using System.IO;
using System.Numerics;

using Heresy.Render.Configuration;
using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class AudioOutputPreferenceTests
{
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(6)]
	[TestCase(8)]
	public void ValidConfigurationsRoundTripWithoutLosingSpeakerOrder(int count)
	{
		WithPreference((preference, path) =>
		{
			RenderConfiguration preset = AudioOutputSettings.Preset(96000, count);
			OutputChannelConfiguration[] channels = new OutputChannelConfiguration[count];
			for (int i = 0; i < count; i++)
			{
				OutputChannelConfiguration speaker = preset.OutputChannels[i];
				channels[i] = new OutputChannelConfiguration(
					new Vector3(speaker.Position.X + i / 16f,
						speaker.Position.Y - i / 8f,
						speaker.Position.Z),
					positionalImportance: i / 2d,
					filterType: i % 3 switch
					{
						1 => OutputFilterType.LowPass,
						2 => OutputFilterType.HighPass,
						_ => OutputFilterType.None,
					},
					cutoffHz: i % 3 == 0 ? null : 1250 + i);
			}
			RenderConfiguration original = new(96000, channels);
			Assert.That(preference.TrySave(original), Is.True);
			RenderConfiguration? restored = new AudioOutputPreference(path).Read();
			Assert.That(restored, Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(restored!.SampleRate, Is.EqualTo(96000));
				Assert.That(restored.OutputChannelCount, Is.EqualTo(count));
			});
			for (int i = 0; i < count; i++)
			{
				Assert.Multiple(() =>
				{
					Assert.That(restored!.OutputChannels[i].Position,
						Is.EqualTo(channels[i].Position));
					Assert.That(restored.OutputChannels[i].PositionalImportance,
						Is.EqualTo(channels[i].PositionalImportance));
					Assert.That(restored.OutputChannels[i].FilterType,
						Is.EqualTo(channels[i].FilterType));
					Assert.That(restored.OutputChannels[i].CutoffHz,
						Is.EqualTo(channels[i].CutoffHz));
				});
			}
			Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"),
				Is.Empty);
		});
	}

	[Test]
	public void SettingIsRestoredAtConstructionAndPersistedOnlyAfterApply()
	{
		WithPreference((preference, path) =>
		{
			AudioOutputSettings defaultSettings = new(preference);
			Assert.That(defaultSettings.Current.SampleRate, Is.EqualTo(48000));
			Assert.That(File.Exists(path), Is.False,
				"First run must not write an invented default.");
			RenderConfiguration selected = AudioOutputSettings.Preset(44100, 6);
			RenderConfiguration oldSnapshot = defaultSettings.Current;
			defaultSettings.Set(selected);
			Assert.That(File.Exists(path), Is.True);
			AudioOutputSettings reopened = new(new AudioOutputPreference(path));
			Assert.Multiple(() =>
			{
				Assert.That(reopened.Current.SampleRate, Is.EqualTo(44100));
				Assert.That(reopened.Current.OutputChannelCount, Is.EqualTo(6));
				Assert.That(oldSnapshot.OutputChannelCount, Is.EqualTo(2),
					"Existing render snapshots remain immutable.");
			});
		});
	}

	[TestCase("{not-json")]
	[TestCase("{\"Version\":2,\"SampleRate\":48000,\"Speakers\":[]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":999999,\"Speakers\":[]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[{}]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[{\"Position\":[0,0,0],\"PositionalImportance\":1,\"FilterType\":99}]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[{\"Position\":[0,0,0],\"PositionalImportance\":1,\"FilterType\":1,\"CutoffHz\":24000}]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[{\"Position\":[1e100,0,0],\"PositionalImportance\":1,\"FilterType\":0}]}")]
	[TestCase("{\"Version\":1,\"SampleRate\":48000,\"Speakers\":[{\"Position\":[0,0,0],\"PositionalImportance\":-1,\"FilterType\":0}]}")]
	public void MalformedUnsupportedOrOutOfRangePreferenceFallsBackToDefault(
		string content)
	{
		WithPreference((preference, path) =>
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			Assert.That(preference.Read(), Is.Null);
			AudioOutputSettings settings = new(preference);
			Assert.That(settings.Current.SampleRate, Is.EqualTo(48000));
			Assert.That(settings.Current.OutputChannelCount, Is.EqualTo(2));
		});
	}

	[Test]
	public void InvalidSaveCannotReplaceLastGoodPreference()
	{
		WithPreference((preference, _) =>
		{
			Assert.That(preference.TrySave(AudioOutputSettings.Preset(44100, 2)),
				Is.True);
			RenderConfiguration unsupported = new(4000,
				[new OutputChannelConfiguration(Vector3.Zero)]);
			Assert.That(preference.TrySave(unsupported), Is.False);
			RenderConfiguration? restored = preference.Read();
			Assert.That(restored!.SampleRate, Is.EqualTo(44100));
			Assert.That(restored.OutputChannelCount, Is.EqualTo(2));
		});
	}

	[Test]
	public void UnavailablePathDoesNotPreventRestorationOrInMemorySelection()
	{
		WithPreference((_, path) =>
		{
			string blocked = Path.Combine(Path.GetDirectoryName(path)!, "blocked");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(blocked, "file-not-directory");
			AudioOutputPreference preference = new(Path.Combine(blocked,
				"audio-output.v1.json"));
			AudioOutputSettings settings = new(preference);
			Assert.That(preference.Read(), Is.Null);
			RenderConfiguration desired = AudioOutputSettings.Preset(88200, 8);
			settings.Set(desired);
			Assert.That(settings.Current, Is.SameAs(desired),
				"Persistence failures cannot undo a valid in-memory Apply.");
			Assert.That(preference.TrySave(desired), Is.False);
		});
	}

	private static void WithPreference(Action<AudioOutputPreference, string> test)
	{
		string folder = Path.Combine(Path.GetTempPath(),
			"heresy-audio-pref-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(folder, "audio-output.v1.json");
		try
		{
			test(new AudioOutputPreference(path), path);
		}
		finally
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, recursive: true);
		}
	}
}
