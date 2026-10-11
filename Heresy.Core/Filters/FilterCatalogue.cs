using System;
using System.Collections.Generic;

namespace Heresy.Core.Filters;

/// <summary>The supported filter designs, independent of the user-facing menu hierarchy.</summary>
public enum FilterKind
{
	SvfLowPass,
	SvfHighPass,
	SvfBandPass,
	SvfNotch,
	BiquadLowPass,
	BiquadHighPass,
	BiquadLowShelf,
	BiquadHighShelf,
	BiquadPeaking,
	BiquadBandPass,
	BiquadNotch,
	LadderLinear,
	LadderSaturated,
	AllPass,
	OnePole,
	CombFeedback,
	FirLowPass,
	FirHighPass,
	FirBandPass,
	FirNotch,
	CombFeedforward,
}

/// <summary>A menu leaf has a Kind; unselectable grouping labels have none.</summary>
public sealed record FilterMenuItem(string Label, int Depth, FilterKind? Kind = null)
{
	public bool IsSelectable => Kind.HasValue;
}

/// <summary>
/// Shared filter-type catalogue. Group rows describe the authoring menu only;
/// processing and parameter lookup always use strongly typed selectable kinds.
/// Parameter descriptors are immutable and may be reused by multiple kinds.
/// </summary>
public static class FilterCatalogue
{
	private static readonly FilterParameterDescriptor Cutoff =
		FilterParameterDescriptor.Number("CutoffHz", 1000, 1, 20000, "Hz");
	private static readonly FilterParameterDescriptor Quality =
		FilterParameterDescriptor.Number("Q", 0.7071067811865476, 0.1, 20);
	private static readonly FilterParameterDescriptor Gain =
		FilterParameterDescriptor.Number("GainDb", 0, -36, 36, "dB");
	private static readonly FilterParameterDescriptor ShelfSlope =
		FilterParameterDescriptor.Number("ShelfSlope", 1, 0.1, 2);
	private static readonly FilterParameterDescriptor Resonance =
		FilterParameterDescriptor.Number("Resonance", 0.25, 0, 1);
	private static readonly FilterParameterDescriptor Drive =
		FilterParameterDescriptor.Number("Drive", 1, 0.1, 10);
	private static readonly FilterParameterDescriptor Oversampling =
		FilterParameterDescriptor.Choice("Oversampling", "2x", "1x", "2x", "4x");
	private static readonly FilterParameterDescriptor AllPassOrder =
		FilterParameterDescriptor.Choice("Order", "1", "1", "2");
	private static readonly FilterParameterDescriptor OnePoleMode =
		FilterParameterDescriptor.Choice("Mode", "LowPass", "LowPass", "HighPass");
	private static readonly FilterParameterDescriptor Delay =
		FilterParameterDescriptor.Number("DelaySeconds", 0.02, 0.001, 2, "s",
			structural: true);
	private static readonly FilterParameterDescriptor Feedback =
		FilterParameterDescriptor.Number("Feedback", 0.5, -0.95, 0.95);
	private static readonly FilterParameterDescriptor Feedforward =
		FilterParameterDescriptor.Number("Feedforward", 0.5, -1, 1);
	private static readonly FilterParameterDescriptor Mix =
		FilterParameterDescriptor.Number("Mix", 0.5, 0, 1);
	private static readonly FilterParameterDescriptor LowCutoff =
		FilterParameterDescriptor.Number("LowCutoffHz", 400, 1, 20000, "Hz");
	private static readonly FilterParameterDescriptor HighCutoff =
		FilterParameterDescriptor.Number("HighCutoffHz", 4000, 1, 20000, "Hz");
	private static readonly FilterParameterDescriptor TapCount =
		FilterParameterDescriptor.Number("TapCount", 127, 3, 2047,
			structural: true, integer: true, odd: true);
	private static readonly FilterParameterDescriptor TransitionWidth =
		FilterParameterDescriptor.Number("TransitionWidthHz", 200, 1, 5000, "Hz",
			structural: true);
	private static readonly FilterParameterDescriptor FirWindow =
		FilterParameterDescriptor.Choice("Window", "Hamming",
			"Hann", "Hamming", "Blackman");
	private static readonly FilterParameterDescriptor PhaseResponse =
		FilterParameterDescriptor.Choice("PhaseResponse", "Linear",
			"Linear", "Minimum", "Maximum");

	private static readonly IReadOnlyList<FilterMenuItem> _menu =
		Array.AsReadOnly(new FilterMenuItem[]
		{
			new("Infinite Impulse Response (IIR) (Cheap)", 0),
			new("State Variable Filter (SVF)", 1),
			new("Low-Pass", 2, FilterKind.SvfLowPass),
			new("High-Pass", 2, FilterKind.SvfHighPass),
			new("Band-Pass", 2, FilterKind.SvfBandPass),
			new("Band-Stop (Notch)", 2, FilterKind.SvfNotch),
			new("Audio EQ Biquads", 1),
			new("Low-Pass (12 dB/octave)", 2, FilterKind.BiquadLowPass),
			new("High-Pass (12 dB/octave)", 2, FilterKind.BiquadHighPass),
			new("Low Shelf", 2, FilterKind.BiquadLowShelf),
			new("High Shelf", 2, FilterKind.BiquadHighShelf),
			new("Peaking / Bell EQ", 2, FilterKind.BiquadPeaking),
			new("Band-Pass", 2, FilterKind.BiquadBandPass),
			new("Band-Stop (Notch)", 2, FilterKind.BiquadNotch),
			new("Ladder (linear)", 1, FilterKind.LadderLinear),
			new("Ladder (non-linear/saturated)", 1, FilterKind.LadderSaturated),
			new("All-Pass (Phase Shift)", 1, FilterKind.AllPass),
			new("One-Pole (6 dB/octave)", 1, FilterKind.OnePole),
			new("Comb (feedback)", 1, FilterKind.CombFeedback),
			new("Finite Impulse Response (FIR) (Expensive)", 0),
			new("Standard Arbitrary FIR", 1),
			new("Low-Pass", 2, FilterKind.FirLowPass),
			new("High-Pass", 2, FilterKind.FirHighPass),
			new("Band-Pass", 2, FilterKind.FirBandPass),
			new("Band-Stop (Notch)", 2, FilterKind.FirNotch),
			new("Comb (feedforward)", 1, FilterKind.CombFeedforward),
		});

	private static readonly IReadOnlyDictionary<FilterKind, IReadOnlyList<FilterParameterDescriptor>>
		_parameters = CreateParameters();

	public static IReadOnlyList<FilterMenuItem> MenuItems => _menu;

	public static IReadOnlyList<FilterParameterDescriptor> GetParameters(FilterKind kind)
	{
		if (!_parameters.TryGetValue(kind, out IReadOnlyList<FilterParameterDescriptor>? parameters))
			throw new ArgumentOutOfRangeException(nameof(kind));
		return parameters;
	}

	private static IReadOnlyDictionary<FilterKind, IReadOnlyList<FilterParameterDescriptor>>
		CreateParameters()
	{
		Dictionary<FilterKind, IReadOnlyList<FilterParameterDescriptor>> result = new();
		void Add(FilterKind kind, params FilterParameterDescriptor[] parameters)
		{
			HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
			foreach (FilterParameterDescriptor descriptor in parameters)
				if (!names.Add(descriptor.Name))
					throw new InvalidOperationException(
						$"Filter {kind} declares duplicate parameter {descriptor.Name}.");
			if (!result.TryAdd(kind, Array.AsReadOnly(parameters)))
				throw new InvalidOperationException($"Duplicate filter kind {kind}.");
		}

		foreach (FilterKind kind in new[]
			{ FilterKind.SvfLowPass, FilterKind.SvfHighPass,
			  FilterKind.SvfBandPass, FilterKind.SvfNotch,
			  FilterKind.BiquadLowPass, FilterKind.BiquadHighPass,
			  FilterKind.BiquadBandPass, FilterKind.BiquadNotch })
			Add(kind, Cutoff, Quality);

		foreach (FilterKind kind in new[]
			{ FilterKind.BiquadLowShelf, FilterKind.BiquadHighShelf })
			Add(kind, Cutoff, ShelfSlope, Gain);
		Add(FilterKind.BiquadPeaking, Cutoff, Quality, Gain);
		Add(FilterKind.LadderLinear, Cutoff, Resonance);
		Add(FilterKind.LadderSaturated, Cutoff, Resonance, Drive, Oversampling);
		Add(FilterKind.AllPass, Cutoff, AllPassOrder);
		Add(FilterKind.OnePole, Cutoff, OnePoleMode);
		Add(FilterKind.CombFeedback, Delay, Feedback, Mix);
		Add(FilterKind.CombFeedforward, Delay, Feedforward, Mix);

		foreach (FilterKind kind in new[]
			{ FilterKind.FirLowPass, FilterKind.FirHighPass })
			Add(kind, Cutoff, TransitionWidth, TapCount, FirWindow, PhaseResponse);
		foreach (FilterKind kind in new[]
			{ FilterKind.FirBandPass, FilterKind.FirNotch })
			Add(kind, LowCutoff, HighCutoff, TransitionWidth,
				TapCount, FirWindow, PhaseResponse);

		// A new kind must never silently appear with no descriptors.
		foreach (FilterKind kind in Enum.GetValues<FilterKind>())
			if (!result.ContainsKey(kind))
				throw new InvalidOperationException($"Missing filter parameters for {kind}.");
		return result;
	}
}
