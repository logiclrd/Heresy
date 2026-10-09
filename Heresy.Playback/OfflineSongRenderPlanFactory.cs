using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Render.Configuration;
using Heresy.Render.Samples;
using Heresy.Scripting.Analysis;

namespace Heresy.Playback;

/// <summary>
/// Owns the same incremental source as realtime playback. Its logical
/// duration is discovered while the file renderer enumerates the song;
/// neither infinite scripts nor looping arrangements are expanded eagerly.
/// </summary>
public sealed class OfflineSongRenderPlan : IDisposable
{
	private readonly PreparedIncrementalPlaybackPlan _playback;

	internal OfflineSongRenderPlan(PreparedIncrementalPlaybackPlan playback)
	{
		_playback = playback ?? throw new ArgumentNullException(nameof(playback));
	}

	public SongDocumentSnapshot Snapshot => _playback.Snapshot;
	public Heresy.Render.Playback.PlaybackSession Session => _playback.Session;
	public PreparedIncrementalAudioSource Source => _playback.Source;
	public TimeSpan LogicalDuration => _playback.Source.LogicalDuration;

	public void Dispose() => _playback.Dispose();
}

/// <summary>
/// Builds a lazy, deterministic root sequence. The Bxx export policy is
/// applied to each actual cursor visit; no full-song compilation occurs.
/// </summary>
public sealed class OfflineSongRenderPlanFactory
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _samples;

	public OfflineSongRenderPlanFactory(RenderConfiguration configuration)
		: this(configuration, new InMemorySampleDataProvider()) { }

	public OfflineSongRenderPlanFactory(
		RenderConfiguration configuration, ISampleDataProvider sampleDataProvider)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		_samples = sampleDataProvider ?? throw new ArgumentNullException(nameof(sampleDataProvider));
	}

	public OfflineSongRenderPlan Create(SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.RootSequenceId.IsNone)
			throw new InvalidOperationException("The song does not have a root sequence.");

		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(document);
		Dictionary<SequenceOrderJumpEncounter, int> visits = [];
		bool FollowJump(SequenceOrderJumpEncounter encounter)
		{
			visits.TryGetValue(encounter, out int count);
			visits[encounter] = ++count;
			// Keep the row containing the third Bxx; suppress its jump.
			return count < 3;
		}
		try
		{
			PreparedIncrementalPlaybackPlan playback =
				new PreparedIncrementalPlaybackFactory(_configuration, _samples)
					.Create(snapshot, snapshot.Document.RootSequenceId,
						shouldFollowOrderJump: FollowJump);
			return new OfflineSongRenderPlan(playback);
		}
		catch (ArgumentException error)
		{
			throw new PlaybackSourceCompilationException(
				$"Could not prepare root sequence {snapshot.Document.RootSequenceId.Value} for offline rendering: {error.Message}",
				Array.Empty<ScriptAnalysisDiagnostic>());
		}
	}
}
