using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Heresy.Core.FmSynthesis;
using Heresy.Render.FmSynthesis;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Live oscillator-inspector preview spanning three waveform cycles, from
/// phase 0.5 at the left to phase 3.5 at the right. This visualizes the
/// normalized waveform before the oscillator's Vmin/Vmax output scaling.
/// </summary>
public sealed class FmSynthWaveformPreview : Control
{
	private static readonly Pen WaveformPen =
		new(Brushes.LightSkyBlue, 1.6);
	private static readonly Pen MidlinePen =
		new(new SolidColorBrush(Color.FromRgb(75, 81, 90)), 1.0);
	private static readonly Pen CyclePen =
		new(new SolidColorBrush(Color.FromRgb(51, 58, 67)), 1.0);

	private FmOscillatorCurvatureShaper? _shaper;
	private bool _configured;

	public FmSynthWaveformPreview(
		FmOscillatorWaveform waveform, double curvature)
	{
		MinHeight = 130;
		ClipToBounds = true;
		SetParameters(waveform, curvature);
	}

	public FmOscillatorWaveform Waveform { get; private set; }
	public double Curvature { get; private set; }

	public void SetParameters(
		FmOscillatorWaveform waveform, double curvature)
	{
		if (_configured && waveform == Waveform && curvature == Curvature)
			return;
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));

		// Table construction occurs only when a parameter is committed in the
		// UI, never for individual graph pixels or audio samples.
		FmOscillatorCurvatureShaper? shaper =
			curvature == 0.0 ? null : new(curvature);
		Waveform = waveform;
		Curvature = curvature;
		_shaper = shaper;
		_configured = true;
		InvalidateVisual();
	}

	public static double PhaseAtFraction(double fraction)
	{
		if (!double.IsFinite(fraction) || fraction < 0.0 || fraction > 1.0)
			throw new ArgumentOutOfRangeException(nameof(fraction));
		return 0.5 + (3.0 * fraction);
	}

	public double ValueAtPhase(double phase)
	{
		double value = FmOscillatorWaveformEvaluator.EvaluateAtPhase(
			Waveform, phase);
		return _shaper is { } shaper ? shaper.Transform(value) : value;
	}

	public double ValueAtFraction(double fraction)
		=> ValueAtPhase(PhaseAtFraction(fraction));

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		double width = Bounds.Width;
		double height = Bounds.Height;
		if (width <= 0.0 || height <= 0.0)
			return;

		context.DrawRectangle(Brushes.Black, null,
			new Rect(0.0, 0.0, width, height));

		// Integer phases 1, 2, 3 are the three cycle boundaries in
		// the requested [0.5, 3.5] viewport.
		for (int cycle = 1; cycle <= 3; cycle++)
		{
			double x = width * ((cycle - 0.5) / 3.0);
			context.DrawLine(CyclePen,
				new Point(x, 0.0), new Point(x, height));
		}
		context.DrawLine(MidlinePen,
			new Point(0.0, height / 2.0),
			new Point(width, height / 2.0));

		const double padding = 5.0;
		double amplitude = Math.Max(0.0, (height / 2.0) - padding);
		int steps = Math.Max(1, (int)Math.Ceiling(width));
		Point previous = new(0.0,
			(height / 2.0) - (ValueAtFraction(0.0) * amplitude));
		for (int column = 1; column <= steps; column++)
		{
			double fraction = (double)column / steps;
			Point next = new(width * fraction,
				(height / 2.0) - (ValueAtFraction(fraction) * amplitude));
			context.DrawLine(WaveformPen, previous, next);
			previous = next;
		}
	}
}
