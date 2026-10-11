using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.Core.Envelopes;

/// <summary>
/// Forward wraps instantly from sustain end to start (and may have a
/// discontinuity). PingPong reverses smoothly at both sustain boundaries.
/// A single-node sustain holds its value until Note Off in either mode.
/// </summary>
public enum EnvelopeSustainLoopMode
{
	Forward,
	PingPong,
	None,
}

/// <summary>A point on the envelope's active time axis. Scalar values are
/// deliberately unrestricted except for finiteness: envelopes also control
/// pitch, panning, and modulation, not just normalized volume.</summary>
public sealed record EnvelopeNode(TimeSpan Time, double Value);

/// <summary>
/// Persistent node-based envelope. Its strictly time-increasing nodes describe
/// a piecewise linear function, and sustain references are *indices*, not
/// timestamps or separately stored levels.
/// Changes replace the complete validated shape atomically.
/// </summary>
public sealed class NodeEnvelopeDefinition : EnvelopeDefinition
{
	private ReadOnlyCollection<EnvelopeNode> _nodes =
		Array.AsReadOnly<EnvelopeNode>([]);

	public NodeEnvelopeDefinition(ObjectId id, string name) : base(id, name)
	{
		// A useful initial volume-like shape, not an imposed value range.
		ReplaceCurve(
			[
				new(TimeSpan.Zero, 0.0),
				new(TimeSpan.FromMilliseconds(10), 1.0),
				new(TimeSpan.FromMilliseconds(110), 0.7),
				new(TimeSpan.FromMilliseconds(410), 0.0),
			],
			sustainStartIndex: 2,
			sustainEndIndex: 2,
			EnvelopeSustainLoopMode.Forward);
	}

	public IReadOnlyList<EnvelopeNode> Nodes => _nodes;
	public int SustainStartIndex { get; private set; }
	public int SustainEndIndex { get; private set; }
	public EnvelopeSustainLoopMode SustainLoopMode { get; private set; }

	/// <summary>Editor viewport only; never affects sample generation.</summary>
	public double Vmin { get; private set; } = 0.0;
	public double Vmax { get; private set; } = 1.0;

	public void SetDisplayRange(double vmin, double vmax)
	{
		ValidateDisplayRange(vmin, vmax);
		Vmin = vmin;
		Vmax = vmax;
	}

	public static void ValidateDisplayRange(double vmin, double vmax)
	{
		if (!double.IsFinite(vmin) || !double.IsFinite(vmax)
			|| !double.IsFinite(vmax - vmin) || vmin >= vmax)
			throw new ArgumentException(
				"Vmin and Vmax must be finite, with Vmin less than Vmax.");
	}

	public void ReplaceCurve(
		IEnumerable<EnvelopeNode> nodes,
		int sustainStartIndex,
		int sustainEndIndex,
		EnvelopeSustainLoopMode sustainLoopMode)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		EnvelopeNode[] candidate = nodes.ToArray();
		Validate(candidate, sustainStartIndex, sustainEndIndex,
			sustainLoopMode);
		_nodes = Array.AsReadOnly(candidate);
		SustainStartIndex = sustainStartIndex;
		SustainEndIndex = sustainEndIndex;
		SustainLoopMode = sustainLoopMode;
	}

	public static void Validate(
		IReadOnlyList<EnvelopeNode> nodes,
		int sustainStartIndex,
		int sustainEndIndex,
		EnvelopeSustainLoopMode sustainLoopMode)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		if (nodes.Count == 0)
			throw new ArgumentException(
				"An envelope must have at least one node.", nameof(nodes));
		if (!Enum.IsDefined(sustainLoopMode))
			throw new ArgumentOutOfRangeException(nameof(sustainLoopMode));
		if (sustainStartIndex < 0 || sustainStartIndex >= nodes.Count)
			throw new ArgumentOutOfRangeException(nameof(sustainStartIndex));
		if (sustainEndIndex < sustainStartIndex
			|| sustainEndIndex >= nodes.Count)
			throw new ArgumentOutOfRangeException(nameof(sustainEndIndex));

		for (int i = 0; i < nodes.Count; i++)
		{
			EnvelopeNode node = nodes[i]
				?? throw new ArgumentException(
					$"Envelope node {i} is null.", nameof(nodes));
			if (node.Time < TimeSpan.Zero
				|| (i == 0 && node.Time != TimeSpan.Zero)
				|| (i > 0 && node.Time <= nodes[i - 1].Time))
			{
				throw new ArgumentException(
					"Envelope node times must start at zero and increase strictly.",
					nameof(nodes));
			}
			if (!double.IsFinite(node.Value))
				throw new ArgumentException(
					$"Envelope node {i} must have a finite value.",
					nameof(nodes));
		}
	}
}
