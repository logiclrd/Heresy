using System;
using System.Collections.Generic;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterInstanceOverrideReplacementTests
{
	[Test]
	public void InheritOverrideAndResetAreDistinctAndNoOpsDoNotAdvanceRevision()
	{
		FilterDefinition definition = new((ObjectId)18U, "Low pass");
		FilterInstance one = new(definition);
		FilterInstance two = new(definition);
		Assert.That(one.Revision, Is.Zero);

		Assert.That(one.ReplaceStageOverrides(1, Array.Empty<KeyValuePair<string,
			FilterParameterValue>>()), Is.False);
		Assert.That(one.Revision, Is.Zero);
		Assert.That(one.ReplaceStageOverrides(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(1000)),
		}), Is.True, "An explicit value equal to the default is still an override.");
		Assert.That(one.GetOverrides(1).Count, Is.EqualTo(1));
		Assert.That(one.Revision, Is.EqualTo(1));
		Assert.That(one.ReplaceStageOverrides(1, new[] {
			Pair("cutoffhz", FilterParameterValue.FromNumber(1000)),
		}), Is.False);
		Assert.That(one.Revision, Is.EqualTo(1));

		definition.SetStageDefaults(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(2500)),
		});
		Assert.That(one.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(1000));
		Assert.That(two.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(2500));

		Assert.That(one.ReplaceStageOverrides(1, Array.Empty<KeyValuePair<string,
			FilterParameterValue>>()), Is.True);
		Assert.That(one.GetOverrides(1), Is.Empty);
		Assert.That(one.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(2500));
		Assert.That(one.Revision, Is.EqualTo(2));
	}

	[Test]
	public void InvalidBatchLeavesEveryOverrideAndRevisionUnchanged()
	{
		FilterDefinition filter = new((ObjectId)19U, "FIR", new[] {
			new FilterStageDefinition(8, "band", FilterKind.FirBandPass),
		});
		FilterInstance use = new(filter);
		Assert.That(use.ReplaceStageOverrides(8, new[] {
			Pair("LowCutoffHz", FilterParameterValue.FromNumber(1200)),
			Pair("HighCutoffHz", FilterParameterValue.FromNumber(5500)),
			Pair("TapCount", FilterParameterValue.FromNumber(255)),
		}), Is.True);
		long version = use.Revision;
		IReadOnlyDictionary<string, FilterParameterValue> previous =
			use.GetOverrides(8);
		Assert.Throws<ArgumentException>(() =>
			use.ReplaceStageOverrides(8, new[] {
				Pair("LowCutoffHz", FilterParameterValue.FromNumber(10000)),
				Pair("HighCutoffHz", FilterParameterValue.FromNumber(3000)),
			}));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			use.ReplaceStageOverrides(8, new[] {
				Pair("TapCount", FilterParameterValue.FromNumber(128)),
			}));
		Assert.Throws<ArgumentException>(() =>
			use.ReplaceStageOverrides(8, new[] {
				Pair("Window", FilterParameterValue.FromChoice("Hann")),
				Pair("window", FilterParameterValue.FromChoice("Blackman")),
			}));
		Assert.That(use.GetOverrides(8), Is.EquivalentTo(previous));
		Assert.That(use.Revision, Is.EqualTo(version));
	}

	[Test]
	public void ReplacementDoesNotModifySiblingUseOrClone()
	{
		FilterDefinition definition = new((ObjectId)20U, "Tone");
		FilterInstance source = new(definition);
		source.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(2200));
		Assert.That(source.Revision, Is.EqualTo(1));
		FilterInstance sibling = source.Clone();
		Assert.That(sibling.Revision, Is.EqualTo(1));
		sibling.ReplaceStageOverrides(1, new[] {
			Pair("Mode", FilterParameterValue.FromChoice("HighPass")),
		});
		Assert.That(source.GetOverrides(1).ContainsKey("CutoffHz"), Is.True);
		Assert.That(source.GetOverrides(1).ContainsKey("Mode"), Is.False);
		Assert.That(sibling.GetOverrides(1).ContainsKey("CutoffHz"), Is.False);
		Assert.That(sibling.GetOverrides(1)["Mode"].ChoiceValue,
			Is.EqualTo("HighPass"));
		Assert.That(definition.GetStage(1).DefaultParameters["Mode"].ChoiceValue,
			Is.EqualTo("LowPass"));
		Assert.That(sibling.Revision, Is.EqualTo(2));
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
