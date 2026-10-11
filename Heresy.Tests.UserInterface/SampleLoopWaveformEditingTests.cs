using System;

using AwesomeAssertions;

using Heresy.Core.Samples;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleLoopWaveformEditingTests
{
	[TestCase(10L, 90L, 10L, 90L)]
	[TestCase(90L, 10L, 10L, 90L)]
	[TestCase(-20L, 250L, 0L, 100L)]
	[TestCase(100L, 100L, 99L, 100L)]
	public void RangeSelectionIsDirectionIndependentAndClampsToPcm(
		long anchor, long end, long expectedStart, long expectedEnd)
	{
		SampleLoop result = SampleLoopWaveformEditing.SelectRange(
			new SampleLoop(SampleLoopMode.None, 0, 0),
			anchor, end, frameCount: 100);
		result.Should().Be(new SampleLoop(SampleLoopMode.Forward,
			expectedStart, expectedEnd));
	}

	[Test]
	public void RangeSelectionPreservesExistingPingPongLoopMode()
	{
		SampleLoopWaveformEditing.SelectRange(
			new SampleLoop(SampleLoopMode.PingPong, 10, 50),
			72, 25, frameCount: 100)
			.Should().Be(new SampleLoop(SampleLoopMode.PingPong, 25, 72));
	}

	[Test]
	public void MovingStartClampsBeforeExclusiveEnd()
	{
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				20,
				80);

		SampleLoop moved =
			SampleLoopWaveformEditing.MoveBoundary(
				loop,
				SampleLoopBoundary.Start,
				requestedFrame: 100,
				frameCount: 120);

		moved.Should().Be(
			new SampleLoop(
				SampleLoopMode.Forward,
				79,
				80));
	}

	[Test]
	public void MovingEndClampsAfterStartAndAtSampleEnd()
	{
		SampleLoop loop =
			new(
				SampleLoopMode.PingPong,
				20,
				80);

		SampleLoopWaveformEditing.MoveBoundary(
				loop,
				SampleLoopBoundary.End,
				requestedFrame: 0,
				frameCount: 120)
			.Should().Be(
				new SampleLoop(
					SampleLoopMode.PingPong,
					20,
					21));

		SampleLoopWaveformEditing.MoveBoundary(
				loop,
				SampleLoopBoundary.End,
				requestedFrame: 999,
				frameCount: 120)
			.Should().Be(
				new SampleLoop(
					SampleLoopMode.PingPong,
					20,
					120));
	}

	[Test]
	public void DraggingNormalizesExistingActiveLoopIntoDecodedRange()
	{
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				50,
				200);

		SampleLoop moved =
			SampleLoopWaveformEditing.MoveBoundary(
				loop,
				SampleLoopBoundary.Start,
				requestedFrame: 40,
				frameCount: 100);

		moved.Should().Be(
			new SampleLoop(
				SampleLoopMode.Forward,
				40,
				100));
	}

	[Test]
	public void InactiveLoopHasNoInteractiveHandles()
	{
		SampleLoop loop =
			new(
				SampleLoopMode.None,
				20,
				80);

		SampleLoopWaveformEditing.TryHitBoundary(
				loop,
				frameCount: 100,
				width: 200,
				x: 40,
				tolerance: 8,
				out _)
			.Should().BeFalse();
	}

	[Test]
	public void HitTestChoosesNearestLoopHandle()
	{
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				25,
				75);

		SampleLoopWaveformEditing.TryHitBoundary(
				loop,
				frameCount: 100,
				width: 200,
				x: 52,
				tolerance: 8,
				out SampleLoopBoundary start)
			.Should().BeTrue();
		start.Should().Be(SampleLoopBoundary.Start);

		SampleLoopWaveformEditing.TryHitBoundary(
				loop,
				frameCount: 100,
				width: 200,
				x: 147,
				tolerance: 8,
				out SampleLoopBoundary end)
			.Should().BeTrue();
		end.Should().Be(SampleLoopBoundary.End);
	}

	[TestCase(-10.0, 0L)]
	[TestCase(0.0, 0L)]
	[TestCase(50.0, 25L)]
	[TestCase(200.0, 100L)]
	[TestCase(250.0, 100L)]
	public void HorizontalPositionMapsToClampedFrameBoundary(
		double x,
		long expected)
	{
		SampleLoopWaveformEditing.XToFrame(
				x,
				width: 200,
				frameCount: 100)
			.Should().Be(expected);
	}

	[Test]
	public void ActiveLoopRequiresDecodedFrames()
	{
		Action action =
			() => SampleLoopWaveformEditing.MoveBoundary(
				new SampleLoop(
					SampleLoopMode.Forward,
					0,
					1),
				SampleLoopBoundary.End,
				requestedFrame: 1,
				frameCount: 0);

		action.Should().Throw<InvalidOperationException>();
	}
}
