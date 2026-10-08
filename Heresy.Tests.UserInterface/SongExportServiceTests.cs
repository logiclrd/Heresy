using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.UserInterface.Exporting;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongExportServiceTests
{
	[Test]
	public async Task ExportWaveRendersRootSequenceAndReplacesDestinationAtomically()
	{
		SongDocument document = CreateSong();
		OfflineSongRenderPlanFactory planFactory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 1000));
		SongExportService service =
			new(planFactory);

		string path =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-export-{Guid.NewGuid():N}.wav");
		await File.WriteAllTextAsync(
			path,
			"old");

		try
		{
			OfflineRenderResult result =
				await service.ExportAsync(
					document,
					path,
					OfflineAudioFileFormat.Wave);

			result.LogicalFrameCount.Should().Be(240);
			result.TailFrameCount.Should().Be(0);
			File.Exists(path).Should().BeTrue();

			byte[] bytes =
				await File.ReadAllBytesAsync(path);
			Encoding.ASCII.GetString(
					bytes,
					0,
					4)
				.Should().Be("RIFF");
			bytes.Length.Should().Be(
				44
					+ checked(
						(int)result.TotalFrameCount
							* 2
							* sizeof(short)));
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	[Test]
	public async Task FailedPlanDoesNotReplaceExistingDestination()
	{
		SongDocument document = new();
		OfflineSongRenderPlanFactory planFactory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 1000));
		SongExportService service =
			new(planFactory);

		string path =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-export-{Guid.NewGuid():N}.wav");
		await File.WriteAllTextAsync(
			path,
			"keep");

		try
		{
			Func<Task> action = () =>
				service.ExportAsync(
					document,
					path,
					OfflineAudioFileFormat.Wave);

			await action.Should()
				.ThrowAsync<InvalidOperationException>();

			(await File.ReadAllTextAsync(path))
				.Should().Be("keep");
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	private static SongDocument CreateSong()
	{
		SongDocument document = new();

		ObjectId patternId =
			document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(
				patternId,
				"Pattern")
			{
				RowCount = 2,
				ChannelCount = 1,
			};
		document.Add(pattern);

		ObjectId sequenceId =
			document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(
				sequenceId,
				"Song");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		document.Add(sequence);

		document.RootSequenceId = sequenceId;
		document.MarkChanged(
			affectsAudio: true);
		return document;
	}
}
