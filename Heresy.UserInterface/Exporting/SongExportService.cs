using System;
using System.IO;
using System.Threading.Tasks;

using Heresy.Core.Objects;
using Heresy.Playback;
using Heresy.Render.File;
using Heresy.Render.Realtime;

namespace Heresy.UserInterface.Exporting;

/// <summary>
/// UI-facing orchestration for deterministic song export. Snapshot/sequence
/// compilation is delegated to Heresy.Playback; PCM rendering/encoding remains
/// in Heresy.Render.File.
/// </summary>
public sealed class SongExportService
{
	private readonly OfflineSongRenderPlanFactory _planFactory;

	public SongExportService(
		OfflineSongRenderPlanFactory planFactory)
	{
		_planFactory =
			planFactory
				?? throw new ArgumentNullException(nameof(planFactory));
	}

	public Task<OfflineRenderResult> ExportAsync(
		SongDocument document,
		string path,
		OfflineAudioFileFormat format)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		// Capture the authoring document synchronously on the caller/UI thread.
		// Everything below this point consumes only the immutable snapshot.
		OfflineSongRenderPlan plan =
			_planFactory.Create(document);
		string fullPath =
			Path.GetFullPath(path);

		return Task.Run(
			() => ExportPlan(
				plan,
				fullPath,
				format));
	}

	private static OfflineRenderResult ExportPlan(
		OfflineSongRenderPlan plan,
		string fullPath,
		OfflineAudioFileFormat format)
	{
		using (plan)
			return ExportOwnedPlan(plan, fullPath, format);
	}

	private static OfflineRenderResult ExportOwnedPlan(
		OfflineSongRenderPlan plan,
		string fullPath,
		OfflineAudioFileFormat format)
	{
		string directory =
			Path.GetDirectoryName(fullPath)
				?? throw new InvalidOperationException(
					"The export destination does not have a parent directory.");
		string temporaryPath =
			Path.Combine(
				directory,
				$".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.heresy-render.tmp");

		try
		{
			OfflineRenderResult result;
			using (FileStream stream =
				new(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None))
			{
				AudioOutputFormat outputFormat =
					new(
						plan.Session.SampleRate,
						plan.Session.OutputChannelCount);
				using IAudioFileSink sink =
					CreateSink(
						stream,
						outputFormat,
						format);
				result =
					OfflinePlaybackRenderer.Render(
						plan.Source,
						sink);
				sink.Complete();
			}

			File.Move(
				temporaryPath,
				fullPath,
				overwrite: true);
			return result;
		}
		catch
		{
			try
			{
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
			catch
			{
			}
			throw;
		}
	}

	private static IAudioFileSink CreateSink(
		Stream stream,
		AudioOutputFormat format,
		OfflineAudioFileFormat fileFormat)
		=> fileFormat switch
		{
			OfflineAudioFileFormat.Flac =>
				new FlacFileSink(
					stream,
					format,
					leaveOpen: true),
			OfflineAudioFileFormat.Mp3 =>
				new Mp3FileSink(
					stream,
					format,
					leaveOpen: true),
			OfflineAudioFileFormat.Wave =>
				new WaveFileSink(
					stream,
					format,
					leaveOpen: true),
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(fileFormat)),
		};
}
