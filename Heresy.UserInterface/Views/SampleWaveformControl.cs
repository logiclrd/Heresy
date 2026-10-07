using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Heresy.Core.Samples;
using Heresy.UserInterface.SampleEditing;

namespace Heresy.UserInterface.Views;

public sealed class SampleWaveformControl : Control
{
	private SamplePcmData? _pcm;
	private SampleWaveformEnvelope? _envelope;
	private int _envelopeColumns;

	public SampleWaveformControl()
	{
		MinHeight = 120;
		ClipToBounds = true;
	}

	public IBrush BackgroundBrush { get; set; } =
		new SolidColorBrush(
			Color.FromRgb(24, 24, 24));

	public IBrush WaveformBrush { get; set; } =
		new SolidColorBrush(
			Color.FromRgb(90, 190, 255));

	public IBrush ZeroLineBrush { get; set; } =
		new SolidColorBrush(
			Color.FromArgb(
				100,
				180,
				180,
				180));

	public IBrush ChannelSeparatorBrush { get; set; } =
		new SolidColorBrush(
			Color.FromArgb(
				90,
				128,
				128,
				128));

	public SamplePcmData? PcmData => _pcm;

	public void SetPcmData(
		SamplePcmData? pcm)
	{
		if (ReferenceEquals(_pcm, pcm))
			return;

		_pcm = pcm;
		_envelope = null;
		_envelopeColumns = 0;
		InvalidateVisual();
	}

	public override void Render(
		DrawingContext context)
	{
		base.Render(context);

		Rect bounds =
			new(Bounds.Size);
		context.FillRectangle(
			BackgroundBrush,
			bounds);

		if (_pcm is null
			|| _pcm.FrameCount == 0
			|| Bounds.Width <= 0
			|| Bounds.Height <= 0)
		{
			return;
		}

		int columns =
			Math.Max(
				1,
				(int)Math.Ceiling(
					Bounds.Width));
		if (_envelope is null
			|| _envelopeColumns != columns)
		{
			_envelope =
				SampleWaveformEnvelope.Build(
					_pcm,
					columns);
			_envelopeColumns = columns;
		}

		double laneHeight =
			Bounds.Height
				/ _pcm.ChannelCount;
		Pen zeroPen =
			new(
				ZeroLineBrush,
				1.0);
		Pen separatorPen =
			new(
				ChannelSeparatorBrush,
				1.0);
		Pen waveformPen =
			new(
				WaveformBrush,
				1.0);

		for (int channel = 0;
			channel < _pcm.ChannelCount;
			channel++)
		{
			double laneTop =
				channel * laneHeight;
			double center =
				laneTop
					+ (laneHeight / 2.0);
			double amplitude =
				Math.Max(
					0.0,
					(laneHeight / 2.0) - 3.0);

			context.DrawLine(
				zeroPen,
				new Point(0.0, center),
				new Point(Bounds.Width, center));

			if (channel > 0)
			{
				context.DrawLine(
					separatorPen,
					new Point(0.0, laneTop),
					new Point(Bounds.Width, laneTop));
			}

			for (int column = 0;
				column < _envelope.ColumnCount;
				column++)
			{
				SampleWaveformPeak peak =
					_envelope[column, channel];
				if (!peak.HasData)
					continue;

				double minimum =
					Math.Clamp(
						peak.Minimum,
						-1.0f,
						1.0f);
				double maximum =
					Math.Clamp(
						peak.Maximum,
						-1.0f,
						1.0f);
				double x =
					(column + 0.5)
						* Bounds.Width
						/ _envelope.ColumnCount;
				double yTop =
					center
						- (maximum * amplitude);
				double yBottom =
					center
						- (minimum * amplitude);

				context.DrawLine(
					waveformPen,
					new Point(x, yTop),
					new Point(x, yBottom));
			}
		}
	}
}
