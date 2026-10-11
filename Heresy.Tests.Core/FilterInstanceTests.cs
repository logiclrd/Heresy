using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterInstanceTests
{
	[Test]
	public void TwoUsesOfOneDefinitionKeepIndependentOverriddenParameters()
	{
		FilterDefinition shared = CreateDefinition();
		FilterInstance one = new(shared);
		FilterInstance two = new(shared);
		one.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(2500));
		two.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(600));
		Assert.That(one.GetStageParameters(10).GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(2500));
		Assert.That(two.GetStageParameters("STAGE1").GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(600));
		Assert.That(shared.GetStage(10).DefaultParameters["CutoffHz"].NumericValue,
			Is.EqualTo(1000));
		Assert.That(one.DefinitionId, Is.EqualTo(shared.Id));
		Assert.That(one.GetOverrides(10).Count, Is.EqualTo(1));
		Assert.That(one.GetOverrides(11).Count, Is.EqualTo(0));
	}

	[Test]
	public void NewDefinitionDefaultsPropagateOnlyWhereNotOverridden()
	{
		FilterDefinition shared = CreateDefinition();
		FilterInstance one = new(shared);
		FilterInstance two = new(shared);
		one.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(2000));
		shared.SetStageDefaults(10, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(800)),
			Pair("Q", FilterParameterValue.FromNumber(3)),
		});
		Assert.That(one.GetStageParameters(10).GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(2000));
		Assert.That(one.GetStageParameters(10).GetParameter("Q").NumericValue,
			Is.EqualTo(3));
		Assert.That(two.GetStageParameters(10).GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(800));
	}

	[Test]
	public void RenameAndReorderMaintainOverridesByStableIdentity()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance instance = new(definition);
		instance.SetStageParameter(11, "CutoffHz",
			FilterParameterValue.FromNumber(2750));
		definition.RenameStage(11, "renamed");
		definition.MoveStage(11, 0);

		IReadOnlyList<ConfiguredFilterStage> stages = instance.GetStages();
		Assert.That(stages.Select(s => s.StageId), Is.EqualTo(new[] { 11, 10 }));
		Assert.That(stages[0].Name, Is.EqualTo("renamed"));
		Assert.That(stages[0].Parameters.GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(2750));
		Assert.That(instance.GetStageParameters("RENAMED")
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(2750));
	}

	[Test]
	public void ChangingAStageTypeTwiceCannotResurrectOldOverrides()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance instance = new(definition);
		instance.SetStageParameter(10, "Q", FilterParameterValue.FromNumber(4));
		definition.ChangeStageKind(10, FilterKind.OnePole);
		definition.ChangeStageKind(10, FilterKind.BiquadLowPass);

		Assert.That(instance.GetOverrides(10), Is.Empty);
		Assert.That(instance.GetStageParameters(10).GetParameter("Q").NumericValue,
			Is.EqualTo(0.7071067811865476));
	}

	[Test]
	public void RemovingAndReaddingAnIdDoesNotRecoverAStaleConfiguration()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance instance = new(definition);
		instance.SetStageParameter(11, "CutoffHz", FilterParameterValue.FromNumber(4000));
		definition.RemoveStage(11);
		definition.ReplaceStages(definition.Stages.Concat(new[] {
			new FilterStageDefinition(11, "replacement", FilterKind.BiquadHighPass),
		}));
		Assert.That(instance.GetOverrides(11), Is.Empty);
		Assert.That(instance.GetStageParameters(11).GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(1000));
	}

	[Test]
	public void BatchValidationFailureDoesNotPartiallyAlterOverrides()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance instance = new(definition);
		instance.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(2200));
		Assert.Throws<ArgumentOutOfRangeException>(() => instance.SetStageParameters(10, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(3000)),
			Pair("Q", FilterParameterValue.FromNumber(-50)),
		}));
		Assert.That(instance.GetStageParameters(10).GetParameter("CutoffHz").NumericValue,
			Is.EqualTo(2200));
		Assert.That(instance.GetOverrides(10).Count, Is.EqualTo(1));
		Assert.Throws<ArgumentException>(() => instance.SetStageParameters(10, new[] {
			Pair("Q", FilterParameterValue.FromNumber(2)),
			Pair("q", FilterParameterValue.FromNumber(5)),
		}));
		Assert.That(instance.GetOverrides(10).Count, Is.EqualTo(1));
		Assert.Throws<KeyNotFoundException>(() =>
			instance.SetStageParameter(98, "Q", FilterParameterValue.FromNumber(3)));
	}

	[Test]
	public void GettingSnapshotsCannotMutateAnUnderlyingUseOrSharedDefinition()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance instance = new(definition);
		FilterSpecificationInstance snapshot = instance.GetStageParameters(10);
		snapshot.SetParameter("CutoffHz", FilterParameterValue.FromNumber(5000));
		FilterSpecificationInstance projected = instance.GetStages()[0].Parameters;
		projected.SetParameter("CutoffHz", FilterParameterValue.FromNumber(6000));
		Assert.That(instance.GetStageParameters(10).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(1000));
		Assert.That(definition.GetStage(10).DefaultParameters["CutoffHz"]
			.NumericValue, Is.EqualTo(1000));
		Assert.That(((IList)instance.GetStages()).IsReadOnly, Is.True);
	}

	[Test]
	public void NestedChainsComposeInnerFirstAndCloneConfigurationOwnership()
	{
		FilterDefinition innerDefinition = CreateDefinition();
		FilterDefinition outerDefinition = new((ObjectId)101U, "Outer");
		FilterInstance inner = new(innerDefinition);
		FilterInstance outer = new(outerDefinition);
		inner.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(400));
		outer.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(4000));

		FilterChain chain = FilterChain.Compose(
			new FilterChain(new[] { inner }),
			new FilterChain(new[] { outer }));
		Assert.That(chain.Count, Is.EqualTo(2));
		Assert.That(chain.Layers.Select(layer => layer.DefinitionId),
			Is.EqualTo(new[] { innerDefinition.Id, outerDefinition.Id }));

		inner.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(900));
		outer.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(9000));
		Assert.That(chain.Layers[0].GetStageParameters(10)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(400));
		Assert.That(chain.Layers[1].GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(4000));

		FilterInstance exported = chain.Layers[0];
		exported.SetStageParameter(10, "CutoffHz", FilterParameterValue.FromNumber(1200));
		Assert.That(chain.Layers[0].GetStageParameters(10)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(400));
		Assert.That(FilterChain.Empty.Count, Is.Zero);
	}

	[Test]
	public void CloningAnInstancePreservesOverridesWithoutSharingThem()
	{
		FilterDefinition definition = CreateDefinition();
		FilterInstance original = new(definition);
		original.SetStageParameter(10, "Q", FilterParameterValue.FromNumber(1.5));
		FilterInstance clone = original.Clone();
		clone.SetStageParameter(10, "Q", FilterParameterValue.FromNumber(4));
		Assert.That(original.GetStageParameters(10).GetParameter("Q").NumericValue,
			Is.EqualTo(1.5));
		Assert.That(clone.GetStageParameters(10).GetParameter("Q").NumericValue,
			Is.EqualTo(4));
		Assert.That(((IDictionary)original.GetOverrides(10)).IsReadOnly, Is.True);
	}

	private static FilterDefinition CreateDefinition()
		=> new((ObjectId)100U, "Shared", new[] {
			new FilterStageDefinition(10, "stage1", FilterKind.BiquadLowPass),
			new FilterStageDefinition(11, "stage2", FilterKind.BiquadHighPass),
		});

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
