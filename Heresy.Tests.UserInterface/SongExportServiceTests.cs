using System;
using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Instruments;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Samples;
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
	public async Task ExportConfigurationChangesApplyOnlyToNewFilesAndPreserveWavHeaders()
	{
		SongDocument document = CreateSong();
		RenderConfiguration selected = RenderConfiguration.Stereo(1000);
		SongExportService service = new(
			new OfflineSongRenderPlanFactory(() => selected));
		string firstPath = Path.Combine(Path.GetTempPath(),
			$"heresy-config-stereo-{Guid.NewGuid():N}.wav");
		string secondPath = Path.Combine(Path.GetTempPath(),
			$"heresy-config-mono-{Guid.NewGuid():N}.wav");

		try
		{
			// The export's immutable snapshot must be captured before the
			// background Task starts, not read repeatedly during PCM output.
			Task<OfflineRenderResult> first = service.ExportAsync(document,
				firstPath, OfflineAudioFileFormat.Wave);
			selected = new RenderConfiguration(2000,
			[
				new OutputChannelConfiguration(System.Numerics.Vector3.Zero),
			]);
			Task<OfflineRenderResult> second = service.ExportAsync(document,
				secondPath, OfflineAudioFileFormat.Wave);
			await Task.WhenAll(first, second);

			byte[] stereo = await File.ReadAllBytesAsync(firstPath);
			byte[] mono = await File.ReadAllBytesAsync(secondPath);
			Assert.That(BinaryPrimitives.ReadInt16LittleEndian(
				stereo.AsSpan(22, 2)), Is.EqualTo(2));
			Assert.That(BinaryPrimitives.ReadInt32LittleEndian(
				stereo.AsSpan(24, 4)), Is.EqualTo(1000));
			Assert.That(BinaryPrimitives.ReadInt16LittleEndian(
				mono.AsSpan(22, 2)), Is.EqualTo(1));
			Assert.That(BinaryPrimitives.ReadInt32LittleEndian(
				mono.AsSpan(24, 4)), Is.EqualTo(2000));
			Assert.That(first.Result.LogicalFrameCount, Is.EqualTo(240));
			Assert.That(second.Result.LogicalFrameCount, Is.EqualTo(480));
		}
		finally
		{
			if (File.Exists(firstPath)) File.Delete(firstPath);
			if (File.Exists(secondPath)) File.Delete(secondPath);
		}
	}

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
	public async Task MixdownContinuesGeneratingItsOwnNotesAfterParentLogicalEnd()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "Delayed PCM", "memory.wav", OneFrameWave()));
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Delayed child")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell late = child.Grid.GetOrCreateCell(2, 0);
		late.SourceId = sampleId;
		late.Note = new StartPatternNote();
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Short parent")
		{
			RowCount = 1, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		document.Add(parent);
		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition root = new(rootId, "Root");
		root.Entries.Add(new SequenceEntry(parentId));
		document.Add(root);
		document.RootSequenceId = rootId;
		SongExportService service = new(new OfflineSongRenderPlanFactory(
			RenderConfiguration.Stereo(sampleRate: 1000)));
		string path = Path.Combine(Path.GetTempPath(),
			$"heresy-export-private-tail-{Guid.NewGuid():N}.wav");
		try
		{
			OfflineRenderResult result = await service.ExportAsync(
				document, path, OfflineAudioFileFormat.Wave);
			result.LogicalFrameCount.Should().Be(120);
			result.TotalFrameCount.Should().BeGreaterThan(240,
				"the child must reach its own row 2 after its parent's end");
			byte[] wav = await File.ReadAllBytesAsync(path);
			int offset = 44 + 240 * 2 * sizeof(short);
			short left = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(offset, 2));
			left.Should().NotBe(0, "the delayed child note must survive tail draining");
		}
		finally
		{
			if (File.Exists(path)) File.Delete(path);
		}
	}

	private static byte[] OneFrameWave()
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(38);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(1000);
			writer.Write(2000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(2);
			writer.Write((short)16384);
		}
		return stream.ToArray();
	}

	[Test]
	public async Task ExportRendersPatternSelectedByInstrumentTone()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "memory.wav", OneFrameWave()));
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Inner")
		{
			RowCount = 1, ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sampleId);
		document.Add(child);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = childId,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 1, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(instrumentId);
		document.Add(parent);
		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition root = new(rootId, "Song");
		root.Entries.Add(new SequenceEntry(parentId));
		document.Add(root);
		document.RootSequenceId = rootId;
		SongExportService service = new(new OfflineSongRenderPlanFactory(
			RenderConfiguration.Stereo(sampleRate: 1000)));
		string path = Path.Combine(Path.GetTempPath(),
			$"heresy-instrument-tone-export-{Guid.NewGuid():N}.wav");
		try
		{
			OfflineRenderResult result = await service.ExportAsync(
				document, path, OfflineAudioFileFormat.Wave);
			result.LogicalFrameCount.Should().Be(120);
			byte[] wav = await File.ReadAllBytesAsync(path);
			short left = BinaryPrimitives.ReadInt16LittleEndian(
				wav.AsSpan(44, 2));
			left.Should().NotBe(0,
				"an Instrument-owned recursive Pattern must produce offline PCM");
		}
		finally
		{
			if (File.Exists(path)) File.Delete(path);
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
