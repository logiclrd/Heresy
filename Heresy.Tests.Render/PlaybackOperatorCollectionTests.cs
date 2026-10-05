using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackOperatorCollectionTests
{
	[Test]
	public void OperatorsOwnIndependentDeltaVectorsAndEffectiveStateSumsThem()
	{
		PlaybackOperatorCollection operators = new();
		LinearRowPlaybackOperator pitch = new(
			PlaybackParameter.PitchLinearUnits,
			totalDelta: 120.0,
			rowSpan: 6.0,
			commitOnExpire: true);
		LinearRowPlaybackOperator vibratoLike = new(
			PlaybackParameter.PitchLinearUnits,
			totalDelta: -24.0,
			rowSpan: 6.0,
			commitOnExpire: false);

		operators.Add(pitch);
		operators.Add(vibratoLike);
		operators.Update(wallTimeSeconds: 0.05, rowTime: 3.0);

		Assert.That(
			pitch.Deltas[PlaybackParameter.PitchLinearUnits],
			Is.EqualTo(60.0));
		Assert.That(
			vibratoLike.Deltas[PlaybackParameter.PitchLinearUnits],
			Is.EqualTo(-12.0));
		Assert.That(
			operators.GetTotalDelta(
				PlaybackParameter.PitchLinearUnits),
			Is.EqualTo(48.0));
	}

	[Test]
	public void ExpiringPersistentOperatorReturnsFinalDeltaForBaselineCommit()
	{
		PlaybackOperatorCollection operators = new();
		LinearRowPlaybackOperator slide = new(
			PlaybackParameter.NoteVolume,
			totalDelta: -0.5,
			rowSpan: 6.0,
			commitOnExpire: true);
		operators.Add(slide);

		PlaybackParameterDeltas committed =
			operators.Expire(
				slide,
				wallTimeSeconds: 0.12,
				rowTime: 6.0);

		Assert.That(
			committed[PlaybackParameter.NoteVolume],
			Is.EqualTo(-0.5));
		Assert.That(
			operators.GetTotalDelta(
				PlaybackParameter.NoteVolume),
			Is.Zero);
	}

	[Test]
	public void ExpiringTransientOperatorDoesNotCommitItsInstantaneousDelta()
	{
		PlaybackOperatorCollection operators = new();
		LinearRowPlaybackOperator modulation = new(
			PlaybackParameter.SpatialX,
			totalDelta: 0.75,
			rowSpan: 6.0,
			commitOnExpire: false);
		operators.Add(modulation);

		PlaybackParameterDeltas committed =
			operators.Expire(
				modulation,
				wallTimeSeconds: 0.12,
				rowTime: 6.0);

		Assert.That(
			committed[PlaybackParameter.SpatialX],
			Is.Zero);
		Assert.That(
			operators.GetTotalDelta(
				PlaybackParameter.SpatialX),
			Is.Zero);
	}
}
