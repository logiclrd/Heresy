using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Diagnostics;
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
		OfflineAudioFileFormat format,
		IProgress<OfflineRenderProgress>? progress = null,
		CancellationToken cancellationToken = default,
		IProgress<SequencingDiagnostic[]>? diagnostics = null,
		WavePcmBitDepth waveBitDepth = WavePcmBitDepth.Pcm16)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		cancellationToken.ThrowIfCancellationRequested();

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
				format,
				progress,
				cancellationToken,
				diagnostics,
				waveBitDepth));
	}

	private static OfflineRenderResult ExportPlan(
		OfflineSongRenderPlan plan,
		string fullPath,
		OfflineAudioFileFormat format,
		IProgress<OfflineRenderProgress>? progress,
		CancellationToken cancellationToken,
		IProgress<SequencingDiagnostic[]>? diagnostics,
		WavePcmBitDepth waveBitDepth)
	{
		using (plan)
		{
			void DrainDiagnostics()
			{
				// Read the same shared bounded log that realtime playback
				// drains, including messages from nested invocation contexts.
				// No UI callback is invoked by a coroutine or audio thread.
				if (diagnostics is null)
					return;
				SequencingDiagnostic[] batch = plan.Diagnostics.Drain();
				if (batch.Length != 0)
					diagnostics.Report(batch);
			}

			// Drain after each successfully written block. A final drain
			// is required if cancellation, a script error or the encoder
			// interrupts processing before another progress event.
			IProgress<OfflineRenderProgress> blockProgress =
				new ExportBlockProgress(progress, DrainDiagnostics);
			try
			{
				return ExportOwnedPlan(plan, fullPath, format,
					blockProgress, cancellationToken, waveBitDepth);
			}
			finally
			{
				DrainDiagnostics();
			}
		}
	}

	private sealed class ExportBlockProgress(
		IProgress<OfflineRenderProgress>? progress,
		Action drainDiagnostics) : IProgress<OfflineRenderProgress>
	{
		public void Report(OfflineRenderProgress update)
		{
			drainDiagnostics();
			progress?.Report(update);
		}
	}

	private static OfflineRenderResult ExportOwnedPlan(
		OfflineSongRenderPlan plan,
		string fullPath,
		OfflineAudioFileFormat format,
		IProgress<OfflineRenderProgress>? progress,
		CancellationToken cancellationToken,
		WavePcmBitDepth waveBitDepth)
	{
		cancellationToken.ThrowIfCancellationRequested();
		AudioOutputFormat outputFormat = new(
			plan.Session.SampleRate, plan.Session.OutputChannelCount);
		// Fail unsupported encoders before creating a temporary output.
		OfflineAudioFileFormats.ValidateOutput(format, outputFormat, waveBitDepth);
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
				using IAudioFileSink sink =
					CreateSink(
						stream,
						outputFormat,
						format,
						waveBitDepth);
				result =
					OfflinePlaybackRenderer.Render(
						plan.Source,
						sink,
						progress: progress,
						cancellationToken: cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();
				sink.Complete();
			}

			// No cancellation is accepted after this atomic commit point:
			// a canceled export must never overwrite an existing file.
			cancellationToken.ThrowIfCancellationRequested();
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
		OfflineAudioFileFormat fileFormat,
		WavePcmBitDepth waveBitDepth)
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
					leaveOpen: true,
					bitDepth: waveBitDepth),
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(fileFormat)),
		};
}
