using System;
using System.IO;
using System.Numerics;
using System.Text.Json;

using Heresy.Render.Configuration;

namespace Heresy.UserInterface;

/// <summary>
/// Optional application-local audio output preference, independent of song
/// files and of currently running immutable render snapshots. Malformed,
/// obsolete or inaccessible settings never prevent application startup.
/// </summary>
public sealed class AudioOutputPreference
{
	private const int CurrentVersion = 1;
	private readonly string _path;

	public AudioOutputPreference(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
	}

	public static AudioOutputPreference ForCurrentUser()
	{
		string root = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(root))
			root = Path.GetTempPath();
		return new AudioOutputPreference(Path.Combine(
			root, "Heresy", "audio-output.v1.json"));
	}

	/// <returns>A completely validated snapshot, or null to use the default.</returns>
	public RenderConfiguration? Read()
	{
		try
		{
			PreferenceDto? dto = JsonSerializer.Deserialize<PreferenceDto>(
				File.ReadAllText(_path));
			if (dto?.Version != CurrentVersion
				|| dto.SampleRate is not int rate
				|| !ValidRate(rate)
				|| dto.Speakers is not { } speakers
				|| !ValidSpeakerCount(speakers.Length))
				return null;

			OutputChannelConfiguration[] channels = new OutputChannelConfiguration[
				speakers.Length];
			for (int index = 0; index < speakers.Length; index++)
			{
				SpeakerDto speaker = speakers[index];
				if (speaker is null
					|| speaker.Position is not { Length: 3 } coordinates
					|| !double.IsFinite(coordinates[0])
					|| !double.IsFinite(coordinates[1])
					|| !double.IsFinite(coordinates[2])
					|| speaker.PositionalImportance is not double importance
					|| !double.IsFinite(importance) || importance < 0
					|| speaker.FilterType is not int filterValue
					|| !ValidFilter(filterValue))
					return null;

				OutputFilterType filter = (OutputFilterType)filterValue;
				double? cutoff = filter == OutputFilterType.None
					? null : speaker.CutoffHz;
				if (filter != OutputFilterType.None
					&& (cutoff is not double value || !ValidCutoff(value, rate)))
					return null;

				channels[index] = new OutputChannelConfiguration(
					new Vector3((float)coordinates[0],
						(float)coordinates[1], (float)coordinates[2]),
					importance, filter, cutoff);
			}
			return new RenderConfiguration(rate, channels);
		}
		catch (Exception ex) when (ex is IOException
			or UnauthorizedAccessException or JsonException
			or ArgumentException or OverflowException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>
	/// Writes only complete supported configurations via a temporary sibling.
	/// Failures are nonfatal and do not change the currently selected output.
	/// </summary>
	public bool TrySave(RenderConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		if (!IsSupported(configuration))
			return false;

		string? directory = Path.GetDirectoryName(_path);
		if (string.IsNullOrWhiteSpace(directory))
			return false;
		string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			SpeakerDto[] speakers = new SpeakerDto[
				configuration.OutputChannelCount];
			for (int i = 0; i < speakers.Length; i++)
			{
				OutputChannelConfiguration channel =
					configuration.OutputChannels[i];
				speakers[i] = new SpeakerDto
				{
					Position = [channel.Position.X, channel.Position.Y,
						channel.Position.Z],
					PositionalImportance = channel.PositionalImportance,
					FilterType = (int)channel.FilterType,
					CutoffHz = channel.FilterType == OutputFilterType.None
						? null : channel.CutoffHz,
				};
			}
			PreferenceDto dto = new()
			{
				Version = CurrentVersion,
				SampleRate = configuration.SampleRate,
				Speakers = speakers,
			};
			string serialized = JsonSerializer.Serialize(dto,
				new JsonSerializerOptions { WriteIndented = true });
			Directory.CreateDirectory(directory);
			File.WriteAllText(temp, serialized);
			File.Move(temp, _path, overwrite: true);
			return true;
		}
		catch (Exception ex) when (ex is IOException
			or UnauthorizedAccessException or JsonException
			or ArgumentException or NotSupportedException)
		{
			return false;
		}
		finally
		{
			try { if (File.Exists(temp)) File.Delete(temp); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	private static bool ValidRate(int rate) => rate is >= 8000 and <= 384000;

	private static bool ValidSpeakerCount(int count)
		=> count is 1 or 2 or 6 or 8;

	private static bool ValidFilter(int value)
		=> value is (int)OutputFilterType.None
			or (int)OutputFilterType.LowPass
			or (int)OutputFilterType.HighPass;

	private static bool ValidCutoff(double cutoff, int sampleRate)
		=> double.IsFinite(cutoff) && cutoff > 0
			&& cutoff < sampleRate / 2d;

	private static bool IsSupported(RenderConfiguration configuration)
	{
		if (!ValidRate(configuration.SampleRate)
			|| !ValidSpeakerCount(configuration.OutputChannelCount))
			return false;
		foreach (OutputChannelConfiguration channel in configuration.OutputChannels)
		{
			if (channel is null || !float.IsFinite(channel.Position.X)
				|| !float.IsFinite(channel.Position.Y)
				|| !float.IsFinite(channel.Position.Z)
				|| !double.IsFinite(channel.PositionalImportance)
				|| channel.PositionalImportance < 0
				|| !ValidFilter((int)channel.FilterType)
				|| (channel.FilterType != OutputFilterType.None
					&& (channel.CutoffHz is not double cutoff
						|| !ValidCutoff(cutoff, configuration.SampleRate))))
				return false;
		}
		return true;
	}

	private sealed class PreferenceDto
	{
		public PreferenceDto() { }
		public int? Version { get; set; }
		public int? SampleRate { get; set; }
		public SpeakerDto[]? Speakers { get; set; }
	}

	private sealed class SpeakerDto
	{
		public SpeakerDto() { }
		public double[]? Position { get; set; }
		public double? PositionalImportance { get; set; }
		public int? FilterType { get; set; }
		public double? CutoffHz { get; set; }
	}
}
