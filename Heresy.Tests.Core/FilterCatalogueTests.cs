using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterCatalogueTests
{
	[Test]
	public void HierarchicalMenuContainsEachSelectableFilterExactlyOnce()
	{
		IReadOnlyList<FilterMenuItem> menu = FilterCatalogue.MenuItems;
		FilterKind[] selected = menu
			.Where(item => item.IsSelectable)
			.Select(item => item.Kind!.Value)
			.ToArray();

		Assert.That(selected.Length, Is.EqualTo(Enum.GetValues<FilterKind>().Length));
		Assert.That(selected, Is.EquivalentTo(Enum.GetValues<FilterKind>()));
		Assert.That(selected.Distinct().Count(), Is.EqualTo(selected.Length));

		Assert.That(menu[0].Label, Does.StartWith("Infinite Impulse Response"));
		Assert.That(menu[0].Depth, Is.EqualTo(0));
		Assert.That(menu[0].IsSelectable, Is.False);
		Assert.That(menu.Single(item => item.Label == "State Variable Filter (SVF)")
			.IsSelectable, Is.False);
		Assert.That(menu.Single(item => item.Label == "Audio EQ Biquads")
			.IsSelectable, Is.False);
		Assert.That(menu.Single(item => item.Label == "Standard Arbitrary FIR")
			.IsSelectable, Is.False);
		Assert.That(menu.Single(item => item.Label.StartsWith("Finite Impulse Response",
			StringComparison.Ordinal)).IsSelectable, Is.False);

		Assert.That(menu.Single(item => item.Kind == FilterKind.CombFeedback)
			.Depth, Is.EqualTo(1));
		Assert.That(menu.Single(item => item.Kind == FilterKind.CombFeedforward)
			.Depth, Is.EqualTo(1));
		Assert.That(Array.IndexOf(selected, FilterKind.CombFeedback),
			Is.LessThan(Array.IndexOf(selected, FilterKind.CombFeedforward)));
		Assert.That(((IList)menu).IsReadOnly, Is.True);
	}

	[Test]
	public void EveryFilterHasNonDuplicatedValidatedDescriptorsAndDefaults()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>())
		{
			IReadOnlyList<FilterParameterDescriptor> descriptors =
				FilterCatalogue.GetParameters(kind);
			Assert.That(descriptors, Is.Not.Empty, kind.ToString());
			Assert.That(descriptors.Select(p => p.Name).Distinct(
				StringComparer.OrdinalIgnoreCase).Count(),
				Is.EqualTo(descriptors.Count), kind.ToString());
			FilterSpecificationInstance instance = new(kind);
			foreach (FilterParameterDescriptor descriptor in descriptors)
			{
				Assert.That(instance.GetParameter(descriptor.Name),
					Is.EqualTo(descriptor.Normalize(descriptor.DefaultValue)),
					$"{kind}.{descriptor.Name}");
				Assert.That(descriptor.IsModulatable,
					Is.EqualTo(descriptor.Kind == FilterParameterValueKind.Number
						&& !descriptor.IsStructural
						&& descriptor.Name != "TransitionWidthHz"));
				Assert.That(descriptor.Name, Does.Not.Contain("."));
			}
			Assert.That(((IList)descriptors).IsReadOnly, Is.True);
		}
		Assert.Throws<ArgumentOutOfRangeException>(
			() => FilterCatalogue.GetParameters((FilterKind)999));
	}

	[Test]
	public void SameRingFirDesignControlsAreNonstructuralButNotFmPorts()
	{
		foreach (FilterKind kind in new[]
			{ FilterKind.FirLowPass, FilterKind.FirHighPass,
			  FilterKind.FirBandPass, FilterKind.FirNotch })
		{
			FilterParameterDescriptor phase = FilterCatalogue.GetParameters(kind)
				.Single(p => p.Name == "PhaseResponse");
			Assert.That(phase.IsStructural, Is.False);
			Assert.That(phase.IsModulatable, Is.False);
			FilterParameterDescriptor window = FilterCatalogue.GetParameters(kind)
				.Single(p => p.Name == "Window");
			Assert.That(window.IsStructural, Is.False);
			Assert.That(window.IsModulatable, Is.False);
			FilterParameterDescriptor width = FilterCatalogue.GetParameters(kind)
				.Single(p => p.Name == "TransitionWidthHz");
			Assert.That(width.IsStructural, Is.False);
			Assert.That(width.IsModulatable, Is.False,
				"worker-prepared design width is not an audio-rate parameter");
			Assert.That(phase.Choices,
				Is.EqualTo(new[] { "Linear", "Minimum", "Maximum" }));
			Assert.That(FilterCatalogue.GetParameters(kind).Single(
				p => p.Name == "TapCount").IsStructural, Is.True);
			Assert.That(FilterCatalogue.GetParameters(kind).Single(
				p => p.Name == "CutoffHz" || p.Name == "LowCutoffHz")
				.IsModulatable, Is.True);
		}
	}

	[Test]
	public void CompatibleLowHighPassModeChoicesAreNotAudioRateFmPorts()
	{
		// A Choice need not change DSP state topology. Both filter families
		// select their low/high output from one existing scalar history.
		foreach (FilterKind kind in new[]
			{ FilterKind.OnePole, FilterKind.SpeakerLegacyRc })
		{
			FilterParameterDescriptor mode = FilterCatalogue.GetParameters(kind)
				.Single(parameter => parameter.Name == "Mode");
			Assert.That(mode.Kind, Is.EqualTo(FilterParameterValueKind.Choice));
			Assert.That(mode.IsStructural, Is.False, kind.ToString());
			Assert.That(mode.IsModulatable, Is.False,
				kind + ": compatible mode is not a sample-rate FM port");
			Assert.That(mode.Choices,
				Is.EqualTo(new[] { "LowPass", "HighPass" }));
			FilterParameterDescriptor cutoff = FilterCatalogue.GetParameters(kind)
				.Single(parameter => parameter.Name == "CutoffHz");
			Assert.That(cutoff.IsModulatable, Is.True);
		}
		Assert.That(FilterParameterDescriptor.Choice("Topology", "A", "A", "B")
			.IsStructural, Is.True, "other Choice contracts remain structural");
	}

	[Test]
	public void SharedParameterConventionsUseSharedImmutableDescriptors()
	{
		FilterParameterDescriptor svf = FilterCatalogue.GetParameters(
			FilterKind.SvfLowPass).Single(p => p.Name == "CutoffHz");
		FilterParameterDescriptor biquad = FilterCatalogue.GetParameters(
			FilterKind.BiquadLowPass).Single(p => p.Name == "CutoffHz");
		Assert.That(ReferenceEquals(svf, biquad), Is.True);
		Assert.That(svf.Units, Is.EqualTo("Hz"));
		Assert.That(svf.Minimum, Is.EqualTo(1));
		Assert.That(FilterCatalogue.GetParameters(FilterKind.LadderSaturated)
			.Single(p => p.Name == "Oversampling").IsStructural, Is.True);
	}

	[Test]
	public void DescriptorFactoriesRejectBrokenParameterContracts()
	{
		Assert.Throws<ArgumentException>(() =>
			FilterParameterDescriptor.Number("X", 1, 2, 0));
		Assert.Throws<ArgumentException>(() =>
			FilterParameterDescriptor.Number("X", 1, 0, double.PositiveInfinity));
		Assert.Throws<ArgumentException>(() =>
			FilterParameterDescriptor.Number("X", 1, 0, 2, odd: true));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			FilterParameterDescriptor.Number("X", double.NaN, 0, 2));
		Assert.Throws<ArgumentException>(() =>
			FilterParameterDescriptor.Choice("Mode", "A", "A", "a"));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			FilterParameterDescriptor.Choice("Mode", "Other", "A", "B"));
	}
}
