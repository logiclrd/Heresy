using System;

using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

public sealed class OfflineSongRenderPlan
{
	internal OfflineSongRenderPlan(
		SongDocumentSnapshot snapshot,
		PlaybackSession session,
		TimeSpan logicalDuration)
	{
		Snapshot =
			snapshot
				?? throw new ArgumentNullException(nameof(snapshot));
		Session =
			session
				?? throw new ArgumentNullException(nameof(session));
		if (logicalDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(logicalDuration));

		LogicalDuration = logicalDuration;
	}

	public SongDocumentSnapshot Snapshot { get; }

	public PlaybackSession Session { get; }

	public TimeSpan LogicalDuration { get; }
}

/// <summary>
/// Captures an immutable authoring snapshot and compiles its root sequence into
/// the same PlaybackSession model used by realtime playback. File encoding is
/// deliberately outside this layer.
/// </summary>
public sealed class OfflineSongRenderPlanFactory
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _sampleDataProvider;

	public OfflineSongRenderPlanFactory(
		RenderConfiguration configuration)
		: this(
			configuration,
			new InMemorySampleDataProvider())
	{
	}

	public OfflineSongRenderPlanFactory(
		RenderConfiguration configuration,
		ISampleDataProvider sampleDataProvider)
	{
		_configuration =
			configuration
				?? throw new ArgumentNullException(nameof(configuration));
		_sampleDataProvider =
			sampleDataProvider
				?? throw new ArgumentNullException(nameof(sampleDataProvider));
	}

	public OfflineSongRenderPlan Create(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.RootSequenceId.IsNone)
		{
			throw new InvalidOperationException(
				"The song does not have a root sequence.");
		}

		SongDocumentSnapshot snapshot =
			SongDocumentSnapshot.Create(document);
		SongDocument snapshotDocument =
			snapshot.Document;
		SongScheduleCompilationResult compilation =
			SongScheduleCompiler.CompileSequence(
				snapshotDocument,
				snapshotDocument.RootSequenceId);

		if (!compilation.Success
			|| compilation.Schedule is null)
		{
			throw new PlaybackSourceCompilationException(
				$"Could not compile root sequence {snapshotDocument.RootSequenceId.Value} for offline rendering.",
				compilation.Diagnostics);
		}

		PlaybackSnapshotSoundResolver resolver =
			new(
				snapshotDocument,
				_sampleDataProvider);
		PlaybackSession session =
			new(
				new RenderContext(_configuration),
				compilation.Schedule,
				resolver);

		return new OfflineSongRenderPlan(
			snapshot,
			session,
			compilation.Duration);
	}
}
