using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;

namespace Heresy.UserInterface.EnvelopeEditing;

/// <summary>Pure pixel-space layout and validated node operations. Avalonia
/// controls use this same geometry for drawing, hit testing, and edits.</summary>
public sealed class NodeEnvelopeGraphGeometry
{
	public const double NodeRadius = 3;
	public const double NodeHitRadius = 4;
	public const double SegmentHitRadius = 6;
	public const double Padding = 16;

	private readonly EnvelopeNode[] _nodes;
	private readonly double _left;
	private readonly double _right;
	private readonly double _top;
	private readonly double _bottom;
	private readonly double _ticksPerPixel;
	private readonly double _valuePerPixel;
	private readonly double _highValue;

	public NodeEnvelopeGraphGeometry(
		IReadOnlyList<EnvelopeNode> nodes, double width, double height)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		if (nodes.Count == 0)
			throw new ArgumentException("A graph needs a node.", nameof(nodes));
		if (!double.IsFinite(width) || !double.IsFinite(height)
			|| width <= 2 * Padding || height <= 2 * Padding)
			throw new ArgumentOutOfRangeException(nameof(width));
		_nodes = nodes.ToArray();
		_left = Padding;
		_right = width - Padding;
		_top = Padding;
		_bottom = height - Padding;
		double maxTicks = Math.Max(1, _nodes[^1].Time.Ticks);
		_ticksPerPixel = maxTicks * 1.15 / (_right - _left);
		double min = _nodes.Min(n => n.Value);
		double max = _nodes.Max(n => n.Value);
		// The plot is not a 0..1 clamp: signed and above-unity values
		// retain their real geometric positions.
		double span = Math.Max(0.2, max - min);
		double margin = span * 0.15;
		_highValue = max + margin;
		_valuePerPixel = (span + 2 * margin) / (_bottom - _top);
	}

	public double Left => _left;
	public double Right => _right;
	public double Top => _top;
	public double Bottom => _bottom;
	public int Count => _nodes.Length;

	public (double X, double Y) Point(int nodeIndex)
		=> (X(_nodes[nodeIndex].Time.Ticks),
			Y(_nodes[nodeIndex].Value));

	public double X(double ticks) => _left + ticks / _ticksPerPixel;
	public double Y(double value) => _top + (_highValue - value) / _valuePerPixel;
	public double TimeTicks(double x) =>
		Math.Max(0, (x - _left) * _ticksPerPixel);
	public double Value(double y) =>
		_highValue - (y - _top) * _valuePerPixel;

	/// <summary>Vertical node-guide hit testing, independent of the
	/// dot's value position. The dot always takes pointer precedence.</summary>
	public int HitGuide(double x, double y)
	{
		if (!double.IsFinite(x) || !double.IsFinite(y)
			|| y < _top || y > _bottom)
			return -1;
		double distance = 4.0;
		int closest = -1;
		for (int i = 0; i < Count; i++)
		{
			double gap = Math.Abs(Point(i).X - x);
			if (gap <= distance)
			{
				distance = gap;
				closest = i;
			}
		}
		return closest;
	}

	/// <summary>During a guide drag, snap to the closest node even when
	/// the pointer lies between vertical guides or outside the viewport.</summary>
	public int ClosestGuide(double x)
	{
		if (!double.IsFinite(x))
			return -1;
		double distance = double.PositiveInfinity;
		int closest = 0;
		for (int i = 0; i < Count; i++)
		{
			double gap = Math.Abs(Point(i).X - x);
			if (gap < distance)
			{
				distance = gap;
				closest = i;
			}
		}
		return closest;
	}

	/// <summary>Endpoint drags cannot cross the opposite endpoint.
	/// Coincident endpoints can be expanded in either direction.</summary>
	public static (int Start, int End) MoveSustainBoundary(
		int start, int end, int boundary, int target)
	{
		if (start > end || target < 0)
			throw new ArgumentOutOfRangeException(nameof(target));
		if (boundary != start && boundary != end)
			throw new ArgumentException(
				"The dragged guide is not a sustain boundary.",
				nameof(boundary));
		if (start == end)
			return (Math.Min(start, target), Math.Max(end, target));
		return boundary == start
			? (Math.Min(target, end), end)
			: (start, Math.Max(start, target));
	}

	/// <summary>Dragging from an unhighlighted guide replaces the
	/// entire region; dragging backward is intentionally symmetric.</summary>
	public static (int Start, int End) SelectSustainRegion(
		int origin, int target, int originalStart, int originalEnd)
	{
		if (origin < 0 || target < 0 || originalStart < 0
			|| originalEnd < originalStart)
			throw new ArgumentOutOfRangeException(nameof(origin));
		// A self-drag must not create a new single-node region. The
		// explicit double-click gesture does that instead.
		if (origin == target)
			return (originalStart, originalEnd);
		return (Math.Min(origin, target), Math.Max(origin, target));
	}

	public int HitNode(double x, double y)
	{
		if (!double.IsFinite(x) || !double.IsFinite(y))
			return -1;
		double limit = NodeHitRadius * NodeHitRadius;
		int closest = -1;
		for (int i = 0; i < Count; i++)
		{
			(double px, double py) = Point(i);
			double square = (px - x) * (px - x) + (py - y) * (py - y);
			if (square <= limit)
			{
				limit = square;
				closest = i;
			}
		}
		return closest;
	}

	/// <summary>Nearest point of a segment, excluding its endpoints.
	/// Returns -1 when the click is not on a visible segment or when
	/// projection rounds to an invalid (duplicate-time) node.</summary>
	public int HitSegment(double x, double y, out EnvelopeNode? projected)
	{
		projected = null;
		if (!double.IsFinite(x) || !double.IsFinite(y))
			return -1;
		double best = SegmentHitRadius * SegmentHitRadius;
		int segment = -1;
		for (int i = 0; i + 1 < Count; i++)
		{
			(double ax, double ay) = Point(i);
			(double bx, double by) = Point(i + 1);
			double dx = bx - ax;
			double dy = by - ay;
			double lengthSquared = dx * dx + dy * dy;
			if (lengthSquared <= 0)
				continue;
			double t = ((x - ax) * dx + (y - ay) * dy) / lengthSquared;
			if (t <= 0 || t >= 1)
				continue;
			double distanceSquared = Math.Pow(x - (ax + dx * t), 2)
				+ Math.Pow(y - (ay + dy * t), 2);
			if (distanceSquared > best)
				continue;
			long start = _nodes[i].Time.Ticks;
			long end = _nodes[i + 1].Time.Ticks;
			long ticks = start + (long)Math.Round((end - (double)start) * t);
			if (ticks <= start || ticks >= end)
				continue;
			double value = _nodes[i].Value
				+ (_nodes[i + 1].Value - _nodes[i].Value) * t;
			if (!double.IsFinite(value))
				continue;
			best = distanceSquared;
			segment = i;
			projected = new EnvelopeNode(TimeSpan.FromTicks(ticks), value);
		}
		return segment;
	}

	public static NodeEnvelopeGraphEdit Insert(
		IReadOnlyList<EnvelopeNode> nodes, int segment,
		EnvelopeNode inserted, int sustainStart, int sustainEnd)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		if (segment < 0 || segment + 1 >= nodes.Count)
			throw new ArgumentOutOfRangeException(nameof(segment));
		if (inserted.Time <= nodes[segment].Time
			|| inserted.Time >= nodes[segment + 1].Time)
			throw new ArgumentException(
				"Inserted node must fall strictly between its neighbours.",
				nameof(inserted));
		EnvelopeNode[] result = nodes.ToArray();
		int index = segment + 1;
		List<EnvelopeNode> modified = result.ToList();
		modified.Insert(index, inserted);
		return new(modified.ToArray(),
			sustainStart >= index ? sustainStart + 1 : sustainStart,
			sustainEnd >= index ? sustainEnd + 1 : sustainEnd,
			index);
	}

	public static NodeEnvelopeGraphEdit? Delete(
		IReadOnlyList<EnvelopeNode> nodes, int index,
		int sustainStart, int sustainEnd)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		if (nodes.Count <= 1)
			return null; // At least one node is required.
		if (index < 0 || index >= nodes.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		List<EnvelopeNode> modified = nodes.ToList();
		modified.RemoveAt(index);
		// Deleting the first node still must leave a valid t=0 anchor.
		// Shift all remaining nodes, preserving their relative spacing.
		if (index == 0)
		{
			long zero = modified[0].Time.Ticks;
			for (int i = 0; i < modified.Count; i++)
				modified[i] = modified[i] with
				{
					Time = TimeSpan.FromTicks(modified[i].Time.Ticks - zero),
				};
		}
		int Map(int old) => old < index ? old
			: old > index ? old - 1 : Math.Min(index, modified.Count - 1);
		return new(modified.ToArray(), Map(sustainStart), Map(sustainEnd),
			-1);
	}

	/// <summary>Moves the node in the *fixed* drag viewport. First
	/// node's time remains zero. Neighbour crossing is clamped rather
	/// than reordering node identities or sustain references.</summary>
	public static EnvelopeNode Move(
		IReadOnlyList<EnvelopeNode> nodes, int index,
		NodeEnvelopeGraphGeometry layout, double x, double y)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		ArgumentNullException.ThrowIfNull(layout);
		if (index < 0 || index >= nodes.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		double desired = layout.TimeTicks(x);
		long ticks = index == 0 ? 0 : (long)Math.Clamp(
			Math.Round(desired), nodes[index - 1].Time.Ticks + 1.0,
			index + 1 == nodes.Count
				? (double)TimeSpan.MaxValue.Ticks
				: nodes[index + 1].Time.Ticks - 1.0);
		double value = layout.Value(y);
		return new EnvelopeNode(TimeSpan.FromTicks(ticks), value);
	}
}

public sealed record NodeEnvelopeGraphEdit(
	IReadOnlyList<EnvelopeNode> Nodes,
	int SustainStartIndex,
	int SustainEndIndex,
	int SelectedNodeIndex);
