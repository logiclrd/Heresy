using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class StreamingFilterChainTests
{
	[Test]
	public void ExplicitBypassPassesInterleavedMultichannelFramesBitForBit()
	{
		FilterDefinition definition = Definition();
		FilterInstance configured = new(definition);
		configured.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(2200));
		FilterChain chain = new(new[] { configured, configured });
		StreamingFilterChain runtime = new(chain, 48000, 2);
		float[] input = { -0.5f, 0.25f, 1f, -1f, 0f, 0.75f, 0.5f, -0.25f };
		float[] expected = (float[])input.Clone();

		runtime.ProcessInterleaved(input);
		Assert.That(input, Is.EqualTo(expected));
		Assert.That(runtime.StageCount, Is.EqualTo(2));
		Assert.That(runtime.PreparedTailFrames, Is.Zero);
		runtime.FinishInput();
		float[] tail = { 1f, 2f, 3f, 4f };
		Assert.That(runtime.RenderTail(tail), Is.Zero);
		Assert.That(tail, Is.EqualTo(new float[4]));
		Assert.Throws<InvalidOperationException>(() =>
			runtime.ProcessFrame(new float[2]));
		runtime.Reset();
		Assert.That(runtime.IsInputFinished, Is.False);
		Assert.That(() => runtime.ProcessFrame(new float[2]), Throws.Nothing);
	}

	[Test]
	public void EmptyChainIsIdentityAndInputShapesAreStrict()
	{
		StreamingFilterChain empty = new(FilterChain.Empty, 44100, 3);
		float[] samples = { -1f, 2f, 5f, 8f, 0f, -5f };
		empty.ProcessInterleaved(samples);
		Assert.That(samples, Is.EqualTo(new[] { -1f, 2f, 5f, 8f, 0f, -5f }));
		Assert.Throws<ArgumentException>(() =>
			empty.ProcessFrame(new float[2]));
		Assert.Throws<ArgumentException>(() =>
			empty.ProcessInterleaved(new float[5]));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(FilterChain.Empty, 0, 2));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(FilterChain.Empty, 44100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(FilterChain.Empty, 44100, 2,
				maximumTailSeconds: double.PositiveInfinity));
	}

	[Test]
	public void ChannelHistoriesAndInvocationHistoriesAreIndependent()
	{
		FilterChain chain = new(new[] { new FilterInstance(Definition()) });
		StreamingFilterChain a = new(chain, 48000, 2, new FeedbackFactory(2));
		StreamingFilterChain b = new(chain, 48000, 2, new FeedbackFactory(2));
		float[] first = { 1f, 0f };
		a.ProcessFrame(first);
		Assert.That(first, Is.EqualTo(new[] { 1f, 0f }));
		float[] next = { 0f, 0f };
		a.ProcessFrame(next);
		Assert.That(next[0], Is.EqualTo(0.5f));
		Assert.That(next[1], Is.Zero);
		float[] other = { 0f, 1f };
		b.ProcessFrame(other);
		Assert.That(other, Is.EqualTo(new[] { 0f, 1f }));
		float[] otherNext = { 0f, 0f };
		b.ProcessFrame(otherNext);
		Assert.That(otherNext, Is.EqualTo(new[] { 0f, 0.5f }));
	}

	[Test]
	public void LayerAndStageOrderAreSerialAndChunkIndependent()
	{
		FilterInstance twoStages = new(new FilterDefinition(
			(ObjectId)12U, "Two", new[] {
				new FilterStageDefinition(1, "first", FilterKind.OnePole),
				new FilterStageDefinition(7, "second", FilterKind.OnePole),
			}));
		FilterChain chain = new(new[] { twoStages });
		StreamingFilterChain entire = new(chain, 48000, 1, new FeedbackFactory(3));
		StreamingFilterChain chunks = new(chain, 48000, 1, new FeedbackFactory(3));
		float[] samples = { 1f, 0f, 0f, 0f, 2f, -1f, 0f, 0f, 0.5f };
		float[] split = (float[])samples.Clone();
		entire.ProcessInterleaved(samples);
		for (int offset = 0; offset < split.Length; offset++)
			chunks.ProcessFrame(split.AsSpan(offset, 1));
		Assert.That(split, Is.EqualTo(samples));
		Assert.That(samples[0], Is.EqualTo(1f));
		Assert.That(samples[1], Is.EqualTo(1f),
			"Two serial stages each retain their own previous-frame output.");

		FilterInstance layer = new(Definition());
		StreamingFilterChain layers = new(new FilterChain(new[] { layer, layer }),
			48000, 1, new FeedbackFactory(3));
		float[] first = { 1f, 0f };
		layers.ProcessInterleaved(first);
		Assert.That(first, Is.EqualTo(new[] { 1f, 1f }));
	}

	[Test]
	public void NonStructuralUpdatesPreserveHistoriesWhileStructuralUpdatesReset()
	{
		FilterDefinition definition = Definition();
		FilterInstance instance = new(definition);
		StreamingFilterChain runtime = new(new FilterChain(new[] { instance }),
			48000, 1, new FeedbackFactory(3));
		float[] first = { 1f };
		runtime.ProcessFrame(first);
		FilterInstance changed = instance.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2000));
		runtime.Configure(new FilterChain(new[] { changed }));
		float[] next = { 0f };
		runtime.ProcessFrame(next);
		Assert.That(next[0], Is.EqualTo(1f),
			"Cutoff changed the plan gain while preserving previous output.");

		changed.SetStageParameter(1, "Mode",
			FilterParameterValue.FromChoice("HighPass"));
		runtime.Configure(new FilterChain(new[] { changed }));
		float[] afterStructural = { 0f };
		runtime.ProcessFrame(afterStructural);
		Assert.That(afterStructural[0], Is.Zero,
			"Structural change must start a fresh per-channel history.");

		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(1000));
		runtime.Configure(new FilterChain(new[] { changed }));
		float[] active = { 1f };
		runtime.ProcessFrame(active);
		definition.ChangeStageKind(1, FilterKind.BiquadLowPass);
		runtime.Configure(new FilterChain(new[] { changed }));
		float[] afterType = { 0f };
		runtime.ProcessFrame(afterType);
		Assert.That(afterType[0], Is.Zero,
			"Type/generation changes invalidate old processing state.");
	}

	[Test]
	public void IncompatibleReorderResetsStateButRenamingDoesNot()
	{
		FilterDefinition definition = new((ObjectId)14U, "Two", new[] {
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
			new FilterStageDefinition(2, "second", FilterKind.OnePole),
		});
		FilterChain chain = new(new[] { new FilterInstance(definition) });
		StreamingFilterChain runtime = new(chain, 48000, 1, new FeedbackFactory(2));
		runtime.ProcessFrame(new float[] { 1f });
		definition.RenameStage(1, "renamed");
		runtime.Configure(chain);
		float[] continuity = { 0f };
		runtime.ProcessFrame(continuity);
		Assert.That(continuity[0], Is.EqualTo(1f));
		definition.MoveStage(2, 0);
		runtime.Configure(chain);
		float[] after = { 0f };
		runtime.ProcessFrame(after);
		Assert.That(after[0], Is.Zero);
	}

	[Test]
	public void FailedPrepareAndFailedStateReconfigureLeaveCurrentAudioUnchanged()
	{
		FilterDefinition definition = Definition();
		FilterInstance instance = new(definition);
		StreamingFilterChain runtime = new(new FilterChain(new[] { instance }),
			48000, 1, new FeedbackFactory(3));
		runtime.ProcessFrame(new float[] { 1f });
		FilterInstance invalid = instance.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(9000));
		Assert.Throws<InvalidOperationException>(() =>
			runtime.Configure(new FilterChain(new[] { invalid })),
			"Fake processor refuses an unsupported reconfiguration.");
		float[] afterFailedReconfigure = { 0f };
		runtime.ProcessFrame(afterFailedReconfigure);
		Assert.That(afterFailedReconfigure[0], Is.EqualTo(0.5f));

		FilterInstance incompatible = instance.Clone();
		incompatible.SetStageParameter(1, "Mode",
			FilterParameterValue.FromChoice("HighPass"));
		incompatible.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(15000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { incompatible }),
				16000, 1, new FeedbackFactory(3)),
			"Preparation must enforce sample-rate-dependent constraints.");
	}

	[Test]
	public void FailedSecondStageDoesNotPartiallyReconfigureFirstStage()
	{
		FilterDefinition definition = new((ObjectId)15U, "Two", new[] {
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
			new FilterStageDefinition(2, "second", FilterKind.OnePole),
		});
		FilterInstance instance = new(definition);
		StreamingFilterChain runtime = new(new FilterChain(new[] { instance }),
			48000, 1, new FeedbackFactory(2));
		runtime.ProcessFrame(new float[] { 1f });
		FilterInstance changed = instance.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2000));
		changed.SetStageParameter(2, "CutoffHz",
			FilterParameterValue.FromNumber(9000));
		Assert.Throws<InvalidOperationException>(() =>
			runtime.Configure(new FilterChain(new[] { changed })));
		float[] next = { 0f };
		runtime.ProcessFrame(next);
		Assert.That(next[0], Is.EqualTo(1f),
			"Both original filter histories and coefficients must survive failed commit.");
	}

	[Test]
	public void ForkDeepCopiesHistoriesAndResetReplayReconstructsExactFrames()
	{
		FilterChain chain = new(new[] { new FilterInstance(Definition()) });
		StreamingFilterChain runtime = new(chain, 48000, 1, new FeedbackFactory(4));
		float[] intro = { 1f, 0f, 0.5f };
		runtime.ProcessInterleaved(intro);
		StreamingFilterChain fork = runtime.Fork();
		float[] continuation = { 0f, 0f, -1f, 0f };
		float[] copied = (float[])continuation.Clone();
		runtime.ProcessInterleaved(continuation);
		fork.ProcessInterleaved(copied);
		Assert.That(copied, Is.EqualTo(continuation));
		fork.Reset();
		float[] rewound = { 1f, 0f, 0.5f, 0f, 0f, -1f, 0f };
		fork.ProcessInterleaved(rewound);
		Assert.That(rewound.Skip(3), Is.EqualTo(continuation));
		// Separate forks do not share history; the original stream has
		// already advanced beyond the fork's reset-and-replay trajectory.
		float[] originalNext = { 0f };
		runtime.ProcessFrame(originalNext);
		float[] newFork = { 1f };
		fork.ProcessFrame(newFork);
		Assert.That(originalNext[0], Is.Not.EqualTo(newFork[0]));
	}

	[Test]
	public void TailRenderingIsFiniteAndBoundedAcrossSerialStages()
	{
		FilterInstance single = new(Definition());
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { single, single }),
			1000, 2, new FeedbackFactory(4), maximumTailSeconds: 0.005);
		Assert.That(runtime.PreparedTailFrames, Is.EqualTo(5),
			"Two three-frame tails are capped to five allowed frames.");
		runtime.ProcessFrame(new float[] { 1f, 0f });
		Assert.Throws<InvalidOperationException>(() =>
			runtime.RenderTail(new float[2]));
		runtime.FinishInput();
		float[] first = new float[6];
		Assert.That(runtime.RenderTail(first), Is.EqualTo(3));
		Assert.That(first[0], Is.GreaterThan(0f));
		Assert.That(first[1], Is.Zero);
		float[] second = new float[8];
		Assert.That(runtime.RenderTail(second), Is.EqualTo(2));
		Assert.That(second.Skip(4), Is.All.Zero);
		Assert.That(runtime.TailRemainingFrames, Is.Zero);
		Assert.That(runtime.RenderTail(new float[8]), Is.Zero);
		StreamingFilterChain cloned = runtime.Fork();
		Assert.That(cloned.IsInputFinished, Is.True);
		cloned.Reset();
		Assert.That(cloned.PreparedTailFrames, Is.EqualTo(5));
		Assert.That(cloned.TailRemainingFrames, Is.Zero);
	}

	private static FilterDefinition Definition()
		=> new((ObjectId)11U, "Echo", new[] {
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
		});

	/// <summary>A test-only recursive processor demonstrating the future
	/// state contract, not a production One-Pole filter implementation.</summary>
	private sealed class FeedbackFactory(int tailFrames) : IStreamingFilterStageFactory
	{
		public IStreamingFilterStagePlan Prepare(
			PreparedFilterStage stage, int sampleRate)
		{
			double cutoff = stage.Parameters["CutoffHz"].NumericValue;
			if (cutoff > sampleRate / 2.0)
				throw new ArgumentOutOfRangeException(nameof(sampleRate),
					"Test processor rejects a cutoff exceeding Nyquist.");
			return new FeedbackPlan(cutoff / 2000.0, tailFrames);
		}
	}

	private sealed class FeedbackPlan(double coefficient, int tailFrames)
		: IStreamingFilterStagePlan
	{
		public int TailFrames => tailFrames;
		public IStreamingFilterStageState CreateState()
			=> new FeedbackState(coefficient, 0f);
	}

	private sealed class FeedbackState(double coefficient, float previous)
		: IStreamingFilterStageState
	{
		private double _coefficient = coefficient;
		private float _previous = previous;

		public float Process(float sample)
		{
			float value = sample + (float)(_previous * _coefficient);
			_previous = value;
			return value;
		}

		public IStreamingFilterStageState Clone()
			=> new FeedbackState(_coefficient, _previous);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not FeedbackPlan replacement)
				throw new ArgumentException("Wrong DSP plan type.", nameof(plan));
			// Prove that a thrown reconfiguration cannot mutate the
			// published state, because the engine configures clones.
			if (replacement.Coefficient > 3.0)
			{
				_previous = -900f;
				throw new InvalidOperationException("Unsupported test coefficient.");
			}
			_coefficient = replacement.Coefficient;
		}
	}
}
