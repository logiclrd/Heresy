using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterSpecificationInstanceTests
{
	[Test]
	public void ParameterNamesAreCaseInsensitiveButChoicesNormalizeToCanonicalSpelling()
	{
		FilterSpecificationInstance instance = new(FilterKind.OnePole);
		Assert.That(instance.GetParameter("mode").ChoiceValue, Is.EqualTo("LowPass"));
		instance.SetParameter("MODE", FilterParameterValue.FromChoice("highpass"));
		Assert.That(instance.GetParameter("Mode").ChoiceValue, Is.EqualTo("HighPass"));
		Assert.That(instance.GetParameter("cutoffhz").NumericValue,
			Is.EqualTo(1000));
	}

	[Test]
	public void ParametersAreIndependentForTwoUsesOfTheSameFilterType()
	{
		FilterSpecificationInstance a = new(FilterKind.BiquadLowPass);
		FilterSpecificationInstance b = new(FilterKind.BiquadLowPass);
		a.SetParameter("CutoffHz", FilterParameterValue.FromNumber(2500));
		a.SetParameter("Q", FilterParameterValue.FromNumber(1.25));
		Assert.That(b.GetParameter("CutoffHz").NumericValue, Is.EqualTo(1000));
		Assert.That(b.GetParameter("Q").NumericValue, Is.EqualTo(0.7071067811865476));
		Assert.That(a.GetParameter("CutoffHz").NumericValue, Is.EqualTo(2500));
		Assert.That(a.Parameters, Is.SameAs(b.Parameters));
	}

	[Test]
	public void InvalidValueKindRangeOrChoiceCannotPartiallyMutateAnInstance()
	{
		FilterSpecificationInstance instance = new(FilterKind.FirLowPass);
		FilterParameterValue cutoff = instance.GetParameter("CutoffHz");

		void Rejected(string name, FilterParameterValue value)
		{
			Assert.That(() => instance.SetParameter(name, value),
				Throws.InstanceOf<ArgumentException>());
			Assert.That(instance.GetParameter("CutoffHz"), Is.EqualTo(cutoff));
		}

		Rejected("CutoffHz", FilterParameterValue.FromNumber(double.NaN));
		Rejected("CutoffHz", FilterParameterValue.FromNumber(double.PositiveInfinity));
		Rejected("CutoffHz", FilterParameterValue.FromNumber(0));
		Rejected("CutoffHz", FilterParameterValue.FromNumber(25000));
		Rejected("CutoffHz", FilterParameterValue.FromChoice("LowPass"));
		Rejected("PhaseResponse", FilterParameterValue.FromChoice("Early"));
		Rejected("PhaseResponse", FilterParameterValue.FromNumber(0));
		Rejected("TapCount", FilterParameterValue.FromNumber(6));
		Rejected("TapCount", FilterParameterValue.FromNumber(127.5));
		Rejected("TapCount", FilterParameterValue.FromNumber(4097));
		Rejected("Unrecognized", FilterParameterValue.FromNumber(2));

		Assert.Throws<KeyNotFoundException>(() => instance.GetParameter("Unknown"));
	}

	[Test]
	public void StructuralValuesCanBeAssignedEvenWithoutAudioRatePorts()
	{
		FilterSpecificationInstance instance = new(FilterKind.FirLowPass);
		instance.SetParameters(new[]
		{
			Assignment("PhaseResponse", FilterParameterValue.FromChoice("minimum")),
			Assignment("TapCount", FilterParameterValue.FromNumber(255)),
			Assignment("Window", FilterParameterValue.FromChoice("blackman")),
			Assignment("CutoffHz", FilterParameterValue.FromNumber(2500)),
		});
		Assert.That(instance.GetParameter("PhaseResponse").ChoiceValue,
			Is.EqualTo("Minimum"));
		Assert.That(instance.GetParameter("TapCount").NumericValue,
			Is.EqualTo(255));
		Assert.That(instance.GetParameter("Window").ChoiceValue,
			Is.EqualTo("Blackman"));
		Assert.That(instance.GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(2500));
		Assert.That(instance.Parameters.Single(p => p.Name == "PhaseResponse")
			.IsModulatable, Is.False);
	}

	[Test]
	public void BandEdgeUpdatesAreAtomicIncludingCombinations()
	{
		FilterSpecificationInstance instance = new(FilterKind.FirBandPass);
		Assert.Throws<ArgumentException>(() =>
			instance.SetParameter("LowCutoffHz", FilterParameterValue.FromNumber(6000)));
		Assert.That(instance.GetParameter("LowCutoffHz").NumericValue, Is.EqualTo(400));

		instance.SetParameters(new[]
		{
			Assignment("LowCutoffHz", FilterParameterValue.FromNumber(6000)),
			Assignment("HighCutoffHz", FilterParameterValue.FromNumber(9000)),
		});
		Assert.That(instance.GetParameter("LowCutoffHz").NumericValue, Is.EqualTo(6000));
		Assert.That(instance.GetParameter("HighCutoffHz").NumericValue, Is.EqualTo(9000));

		Assert.Throws<ArgumentException>(() => instance.SetParameters(new[]
		{
			Assignment("LowCutoffHz", FilterParameterValue.FromNumber(1000)),
			Assignment("HighCutoffHz", FilterParameterValue.FromNumber(800)),
		}));
		Assert.That(instance.GetParameter("LowCutoffHz").NumericValue, Is.EqualTo(6000));
		Assert.That(instance.GetParameter("HighCutoffHz").NumericValue, Is.EqualTo(9000));
	}

	[Test]
	public void ARejectedBatchOrDuplicatePathsLeaveEarlierValuesUntouched()
	{
		FilterSpecificationInstance instance = new(FilterKind.BiquadPeaking);
		double oldQ = instance.GetParameter("Q").NumericValue;
		Assert.Throws<ArgumentException>(() => instance.SetParameters(new[]
		{
			Assignment("Q", FilterParameterValue.FromNumber(2)),
			Assignment("NoSuchParameter", FilterParameterValue.FromNumber(1)),
		}));
		Assert.That(instance.GetParameter("Q").NumericValue, Is.EqualTo(oldQ));

		Assert.Throws<ArgumentException>(() => instance.SetParameters(new[]
		{
			Assignment("GainDb", FilterParameterValue.FromNumber(8)),
			Assignment("gaindb", FilterParameterValue.FromNumber(-8)),
		}));
		Assert.That(instance.GetParameter("GainDb").NumericValue, Is.EqualTo(0));
	}

	[Test]
	public void StructuralParameterValidationIsNotJustEnumChecking()
	{
		FilterSpecificationInstance instance = new(FilterKind.LadderSaturated);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			instance.SetParameter("Oversampling", FilterParameterValue.FromChoice("8x")));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			instance.SetParameter("Drive", FilterParameterValue.FromNumber(-1)));
		instance.SetParameter("Oversampling", FilterParameterValue.FromChoice("4X"));
		Assert.That(instance.GetParameter("Oversampling").ChoiceValue,
			Is.EqualTo("4x"));
	}

	private static KeyValuePair<string, FilterParameterValue> Assignment(
		string name, FilterParameterValue value)
		=> new(name, value);
}
