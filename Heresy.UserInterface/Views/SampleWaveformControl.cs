using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Heresy.Core.Samples;
using Heresy.UserInterface.SampleEditing;

namespace Heresy.UserInterface.Views;

public sealed class SampleLoopChangedEventArgs : EventArgs
{
	public SampleLoopChangedEventArgs(
		SampleLoop loop)
	{
		Loop = loop
			?? throw new ArgumentNullException(nameof(loop));
	}

	public SampleLoop Loop { get; }
}

public sealed class SampleWaveformControl : Control
{
	private const double LoopHandleHitTolerance = 8.0;

	private SamplePcmData? _pcm;
	private SampleWaveformEnvelope? _envelope;
	private int _envelopeColumns;
	private SampleLoop _loop =
		new(
			SampleLoopMode.None,
			0,
			0);
	private SampleLoopBoundary? _dragBoundary;
	private SampleLoop? _dragOriginalLoop;
	private long? _dragAnchorFrame;
	private double _dragAnchorX;
	private bool _rangeDragged;

	public SampleWaveformControl()
	{
		MinHeight = 120;
		ClipToBounds = true;
	}

	public event EventHandler<SampleLoopChangedEventArgs>? LoopPreviewChanged;

	public event EventHandler<SampleLoopChangedEventArgs>? LoopCommitted;

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

	public IBrush LoopRangeBrush { get; set; } =
		new SolidColorBrush(
			Color.FromArgb(
				52,
				80,
				220,
				120));

	public IBrush LoopHandleBrush { get; set; } =
		new SolidColorBrush(
			Color.FromRgb(
				120,
				235,
				145));

	public SamplePcmData? PcmData => _pcm;

	public SampleLoop Loop => _loop;

	public void SetPcmData(
		SamplePcmData? pcm)
	{
		if (ReferenceEquals(_pcm, pcm))
			return;

		_pcm = pcm;
		_envelope = null;
		_envelopeColumns = 0;
		CancelDrag();
		InvalidateVisual();
	}

	public void SetLoop(
		SampleLoop loop)
	{
		ArgumentNullException.ThrowIfNull(loop);
		if (_loop == loop)
			return;

		_loop = loop;
		CancelDrag();
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

		SampleLoop? displayLoop =
			GetDisplayLoop();
		if (displayLoop is not null)
			DrawLoopRange(context, displayLoop);

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

		if (displayLoop is not null)
			DrawLoopHandles(context, displayLoop);
	}

	protected override void OnPointerPressed(
		PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);

		if (_pcm is null
			|| _pcm.FrameCount == 0
			|| Bounds.Width <= 0)
		{
			return;
		}

		PointerPoint point =
			e.GetCurrentPoint(this);
		if (!point.Properties.IsLeftButtonPressed)
			return;

		double x =
			e.GetPosition(this).X;
		_dragOriginalLoop = _loop;
		if (SampleLoopWaveformEditing.TryHitBoundary(
			_loop, _pcm.FrameCount, Bounds.Width, x,
			LoopHandleHitTolerance, out SampleLoopBoundary boundary))
		{
			_dragBoundary = boundary;
		}
		else
		{
			_dragAnchorFrame = SampleLoopWaveformEditing.XToFrame(
				x, Bounds.Width, _pcm.FrameCount);
			_dragAnchorX = x;
			_rangeDragged = false;
		}
		e.Pointer.Capture(this);
		e.Handled = true;
	}

	protected override void OnPointerMoved(
		PointerEventArgs e)
	{
		base.OnPointerMoved(e);

		if ((_dragBoundary is null && _dragAnchorFrame is null)
			|| _pcm is null
			|| _pcm.FrameCount == 0
			|| Bounds.Width <= 0)
		{
			return;
		}

		long frame =
			SampleLoopWaveformEditing.XToFrame(
				e.GetPosition(this).X,
				Bounds.Width,
				_pcm.FrameCount);
		if (_dragAnchorFrame.HasValue
			&& Math.Abs(e.GetPosition(this).X - _dragAnchorX) >= 3.0)
			_rangeDragged = true;
		if (_dragAnchorFrame.HasValue && !_rangeDragged)
			return;
		SampleLoop candidate = _dragBoundary is SampleLoopBoundary boundary
			? SampleLoopWaveformEditing.MoveBoundary(
				_loop, boundary, frame, _pcm.FrameCount)
			: SampleLoopWaveformEditing.SelectRange(
				_dragOriginalLoop!, _dragAnchorFrame!.Value, frame,
				_pcm.FrameCount);
		if (candidate == _loop)
			return;

		_loop = candidate;
		InvalidateVisual();
		LoopPreviewChanged?.Invoke(
			this,
			new SampleLoopChangedEventArgs(
				candidate));
		e.Handled = true;
	}

	protected override void OnPointerReleased(
		PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);

		if (_dragBoundary is null && _dragAnchorFrame is null)
			return;

		SampleLoop? original = _dragOriginalLoop;
		if (_pcm is not null && _pcm.FrameCount > 0 && Bounds.Width > 0
			&& _dragAnchorFrame is long anchor)
		{
			// Pointer release may be the first update since the press.
			double x = e.GetPosition(this).X;
			if (Math.Abs(x - _dragAnchorX) >= 3.0)
				_rangeDragged = true;
			if (_rangeDragged)
			{
				_loop = SampleLoopWaveformEditing.SelectRange(
					original!, anchor,
					SampleLoopWaveformEditing.XToFrame(
						x, Bounds.Width, _pcm.FrameCount),
					_pcm.FrameCount);
				InvalidateVisual();
				LoopPreviewChanged?.Invoke(this,
					new SampleLoopChangedEventArgs(_loop));
			}
		}
		bool commit = _dragBoundary is not null || _rangeDragged;
		CancelDrag();
		e.Pointer.Capture(null);
		e.Handled = true;

		if (commit && original != _loop)
		{
			LoopCommitted?.Invoke(
				this,
				new SampleLoopChangedEventArgs(
					_loop));
		}
	}

	protected override void OnPointerCaptureLost(
		PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);

		if (_dragBoundary is null && _dragAnchorFrame is null)
			return;

		SampleLoop? original = _dragOriginalLoop;
		CancelDrag();
		if (original is not null
			&& original != _loop)
		{
			_loop = original;
			InvalidateVisual();
			LoopPreviewChanged?.Invoke(
				this,
				new SampleLoopChangedEventArgs(
					original));
		}
	}

	private void DrawLoopRange(
		DrawingContext context,
		SampleLoop loop)
	{
		if (_pcm is null)
			return;

		double startX =
			SampleLoopWaveformEditing.FrameToX(
				loop.StartFrame,
				Bounds.Width,
				_pcm.FrameCount);
		double endX =
			SampleLoopWaveformEditing.FrameToX(
				loop.EndFrameExclusive,
				Bounds.Width,
				_pcm.FrameCount);

		context.FillRectangle(
			LoopRangeBrush,
			new Rect(
				startX,
				0,
				Math.Max(0.0, endX - startX),
				Bounds.Height));
	}

	private void DrawLoopHandles(
		DrawingContext context,
		SampleLoop loop)
	{
		if (_pcm is null)
			return;

		Pen handlePen =
			new(
				LoopHandleBrush,
				2.0);
		double startX =
			Math.Clamp(
				SampleLoopWaveformEditing.FrameToX(
					loop.StartFrame,
					Bounds.Width,
					_pcm.FrameCount),
				1.0,
				Math.Max(1.0, Bounds.Width - 1.0));
		double endX =
			Math.Clamp(
				SampleLoopWaveformEditing.FrameToX(
					loop.EndFrameExclusive,
					Bounds.Width,
					_pcm.FrameCount),
				1.0,
				Math.Max(1.0, Bounds.Width - 1.0));

		context.DrawLine(
			handlePen,
			new Point(startX, 0),
			new Point(startX, Bounds.Height));
		context.DrawLine(
			handlePen,
			new Point(endX, 0),
			new Point(endX, Bounds.Height));

		const double capWidth = 8.0;
		const double capHeight = 8.0;
		context.FillRectangle(
			LoopHandleBrush,
			new Rect(
				startX,
				0,
				capWidth,
				capHeight));
		context.FillRectangle(
			LoopHandleBrush,
			new Rect(
				Math.Max(0.0, endX - capWidth),
				0,
				capWidth,
				capHeight));
	}

	private SampleLoop? GetDisplayLoop()
	{
		if (_pcm is null
			|| _pcm.FrameCount == 0
			|| _loop.Mode == SampleLoopMode.None)
		{
			return null;
		}

		return SampleLoopWaveformEditing.MoveBoundary(
			_loop,
			SampleLoopBoundary.End,
			_loop.EndFrameExclusive,
			_pcm.FrameCount);
	}

	private void CancelDrag()
	{
		_dragBoundary = null;
		_dragOriginalLoop = null;
		_dragAnchorFrame = null;
		_rangeDragged = false;
	}
}
