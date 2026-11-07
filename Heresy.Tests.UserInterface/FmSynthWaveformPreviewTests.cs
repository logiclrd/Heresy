using System;

using Avalonia;
using Avalonia.Headless.NUnit;

using Heresy.Core.FmSynthesis;
using Heresy.Render.FmSynthesis;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthWaveformPreviewTests
{
	[Test]
	public void HorizontalExtentIsPreciselyThreeCyclesStartingAtPhaseOneHalf()
	{
		Assert.That(FmSynthWaveformPreview.PhaseAtFraction(0.0),
			Is.EqualTo(0.5));
		Assert.That(FmSynthWaveformPreview.PhaseAtFraction(1.0 / 3.0),
			Is.EqualTo(1.5).Within(1e-14));
		Assert.That(FmSynthWaveformPreview.PhaseAtFraction(2.0 / 3.0),
			Is.EqualTo(2.5).Within(1e-14));
		Assert.That(FmSynthWaveformPreview.PhaseAtFraction(1.0),
			Is.EqualTo(3.5));
		Assert.That(() => FmSynthWaveformPreview.PhaseAtFraction(-0.01),
			Throws.TypeOf<ArgumentOutOfRangeException>());
		Assert.That(() => FmSynthWaveformPreview.PhaseAtFraction(1.01),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[TestCase(FmOscillatorWaveform.Sine, -8.0)]
	[TestCase(FmOscillatorWaveform.Sine, 0.0)]
	[TestCase(FmOscillatorWaveform.Sine, 8.0)]
	[TestCase(FmOscillatorWaveform.Triangle, -2.0)]
	[TestCase(FmOscillatorWaveform.Triangle, 0.0)]
	[TestCase(FmOscillatorWaveform.Triangle, 2.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, -32.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, 0.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, 32.0)]
	[TestCase(FmOscillatorWaveform.Square, -8.0)]
	[TestCase(FmOscillatorWaveform.Square, 0.0)]
	[TestCase(FmOscillatorWaveform.Square, 8.0)]
	public void PreviewUsesTheExactRendererWaveformAndCurvatureMapping(
		FmOscillatorWaveform waveform, double curvature)
	{
		FmSynthWaveformPreview preview = new(waveform, curvature);
		FmOscillatorCurvatureShaper? shaper = curvature == 0.0
			? null : new FmOscillatorCurvatureShaper(curvature);
		for (int i = 0; i <= 197; i++)
		{
			double fraction = (double)i / 197;
			double phase = 0.5 + (3.0 * fraction);
			double native = FmOscillatorWaveformEvaluator.EvaluateAtPhase(
				waveform, phase);
			double expected = shaper is null ? native : shaper.Transform(native);
			Assert.That(preview.ValueAtFraction(fraction),
				Is.EqualTo(expected).Within(1e-12),
				$"waveform {waveform}, k {curvature}, x {fraction}");
		}
		Assert.That(preview.ValueAtFraction(0.0),
			Is.EqualTo(preview.ValueAtFraction(1.0)).Within(1e-12));
	}

	[TestCase(2.0)]
	[TestCase(-2.0)]
	public void SawtoothPreviewMatchesEndpointAnchoredAnalyticCurve(double curvature)
	{
		FmSynthWaveformPreview preview =
			new(FmOscillatorWaveform.Sawtooth, curvature);
		for (int i = 0; i <= 100; i++)
		{
			// Phase 1..2 is a full sawtooth cycle, including the reset at 1.
			double phase = 1.0 + (i / 101.0);
			double native = (2.0 * (phase - 1.0)) - 1.0;
			double expected = 2.0 * (Math.Exp(curvature * (native + 1.0))
				- 1.0) / (Math.Exp(2.0 * curvature) - 1.0) - 1.0;
			Assert.That(preview.ValueAtPhase(phase),
				Is.EqualTo(expected).Within(0.00007));
		}
	}

	[Test]
	public void ParameterEditsAreAtomicAndSquareRetainsOnlyEndpointValues()
	{
		FmSynthWaveformPreview preview =
			new(FmOscillatorWaveform.Sawtooth, 2.0);
		Assert.That(() => preview.SetParameters(
			FmOscillatorWaveform.Sine, 33.0),
			Throws.TypeOf<ArgumentOutOfRangeException>());
		Assert.That(preview.Waveform, Is.EqualTo(FmOscillatorWaveform.Sawtooth));
		Assert.That(preview.Curvature, Is.EqualTo(2.0));
		preview.SetParameters(FmOscillatorWaveform.Square, -32.0);
		Assert.That(preview.Waveform, Is.EqualTo(FmOscillatorWaveform.Square));
		Assert.That(preview.Curvature, Is.EqualTo(-32.0));
		Assert.That(preview.ValueAtPhase(1.25), Is.EqualTo(1.0));
		Assert.That(preview.ValueAtPhase(1.75), Is.EqualTo(-1.0));
	}

	[AvaloniaTest]
	public void PreviewCanResizeWithoutChangingItsPhaseMapping()
	{
		FmSynthWaveformPreview preview =
			new(FmOscillatorWaveform.Sine, 0.0);
		preview.Measure(new Size(120.0, 150.0));
		preview.Arrange(new Rect(0.0, 0.0, 120.0, 150.0));
		Assert.That(preview.Bounds.Width, Is.EqualTo(120.0));
		preview.Measure(new Size(300.0, 200.0));
		preview.Arrange(new Rect(0.0, 0.0, 300.0, 200.0));
		Assert.That(preview.Bounds.Width, Is.EqualTo(300.0));
		Assert.That(preview.ValueAtFraction(0.0),
			Is.EqualTo(preview.ValueAtFraction(1.0)).Within(1e-12));
	}
}
