using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterDefinitionTests
{
	[Test]
	public void ADefinitionIsAnIdentifiedSongObjectWithOneValidDefaultStage()
	{
		FilterDefinition definition = new((ObjectId)17U, "Lowpass");
		Assert.That(definition.Id, Is.EqualTo((ObjectId)17U));
		Assert.That(definition.Kind, Is.EqualTo(SongObjectKind.Filter));
		Assert.That(definition.Stages.Count, Is.EqualTo(1));
		Assert.That(definition.Stages[0].StageId, Is.EqualTo(1));
		Assert.That(definition.Stages[0].Name, Is.EqualTo("stage1"));
		Assert.That(definition.Stages[0].Kind, Is.EqualTo(FilterKind.OnePole));
		Assert.That(((IList)definition.Stages).IsReadOnly, Is.True);
	}

	[Test]
	public void RenameAndReorderPreserveStableStageIdentityAndDefaults()
	{
		FilterDefinition definition = new((ObjectId)1U, "Shape",
			new[] {
				new FilterStageDefinition(9, "bass", FilterKind.BiquadLowPass),
				new FilterStageDefinition(4, "treble", FilterKind.BiquadHighPass),
			});
		definition.SetStageDefaults(9, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(220)),
		});
		definition.RenameStage(9, "deep_bass");
		definition.MoveStage(9, 1);

		Assert.That(definition.Stages.Select(s => s.StageId), Is.EqualTo(new[] { 4, 9 }));
		Assert.That(definition.Stages[1].Name, Is.EqualTo("deep_bass"));
		Assert.That(definition.Stages[1].DefaultParameters["CutoffHz"].NumericValue,
			Is.EqualTo(220));
		Assert.That(definition.GetStage("DEEP_BASS").StageId, Is.EqualTo(9));
		Assert.That(definition.GetStage(9).Kind, Is.EqualTo(FilterKind.BiquadLowPass));
	}

	[Test]
	public void StageIdsAreMonotonicAndNeverReusedByAutomaticInsertion()
	{
		FilterDefinition definition = new((ObjectId)2U, "Filter");
		int second = definition.AddStage("second", FilterKind.SvfLowPass).StageId;
		definition.RemoveStage(second);
		int third = definition.AddStage("third", FilterKind.FirLowPass).StageId;
		Assert.That(third, Is.GreaterThan(second));
		Assert.That(definition.Stages.Select(s => s.StageId), Is.EqualTo(new[] { 1, third }));
		Assert.That(() => definition.RemoveStage(third), Throws.Nothing);
		Assert.Throws<ArgumentException>(() => definition.RemoveStage(1));
		Assert.That(definition.Stages.Single().Name, Is.EqualTo("stage1"));
	}

	[Test]
	public void NamesAreValidUnambiguousIdentifiersAndUniqueIgnoringCase()
	{
		foreach (string name in new[] { "", " ", "2nd", "with.dots", "a-b", "a b",
			"é", "dot#", "a/b" })
			Assert.Throws<ArgumentException>(() =>
				new FilterStageDefinition(1, name, FilterKind.OnePole),
				$"Rejected name: {name}");

		FilterDefinition definition = new((ObjectId)3U, "Filter");
		definition.AddStage("_stage_20", FilterKind.OnePole);
		Assert.Throws<ArgumentException>(() =>
			definition.AddStage("_STAGE_20", FilterKind.BiquadNotch));
		Assert.That(definition.Stages.Count, Is.EqualTo(2));
	}

	[Test]
	public void InvalidReplacementNeverPartiallyChangesTheDefinition()
	{
		FilterDefinition definition = new((ObjectId)4U, "Filter");
		FilterStageDefinition original = definition.Stages[0];
		void Rejected(IEnumerable<FilterStageDefinition> stages)
		{
			Assert.That(() => definition.ReplaceStages(stages),
				Throws.InstanceOf<ArgumentException>());
			Assert.That(definition.Stages.Count, Is.EqualTo(1));
			Assert.That(definition.Stages[0], Is.SameAs(original));
		}
		Rejected([]);
		Rejected(new FilterStageDefinition[] { original, original });
		Rejected(new[] {
			original,
			new FilterStageDefinition(2, "STAGE1", FilterKind.FirHighPass),
		});
		Assert.Throws<ArgumentOutOfRangeException>(() => definition.MoveStage(1, 5));
		Assert.Throws<KeyNotFoundException>(() => definition.RenameStage(99, "absent"));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			definition.ChangeStageKind(1, (FilterKind)900));
		Assert.That(definition.Stages[0], Is.SameAs(original));
		Assert.That(definition.AddStage("stage2", FilterKind.OnePole).StageId,
			Is.EqualTo(2), "failed mutations must not consume stage identities");
	}

	[Test]
	public void DefaultsRejectInvalidCombinationsAtomically()
	{
		FilterDefinition definition = new((ObjectId)5U, "Filter",
			new[] { new FilterStageDefinition(30, "band", FilterKind.FirBandPass) });
		Assert.Throws<ArgumentException>(() =>
			definition.SetStageDefaults(30, new[] {
				Pair("LowCutoffHz", FilterParameterValue.FromNumber(10000)),
			}));
		Assert.That(definition.GetStage(30).DefaultParameters["LowCutoffHz"]
			.NumericValue, Is.EqualTo(400));

		definition.SetStageDefaults(30, new[] {
			Pair("LowCutoffHz", FilterParameterValue.FromNumber(8000)),
			Pair("HighCutoffHz", FilterParameterValue.FromNumber(12000)),
		});
		Assert.That(definition.GetStage(30).DefaultParameters["LowCutoffHz"]
			.NumericValue, Is.EqualTo(8000));
		Assert.That(definition.GetStage(30).DefaultParameters["HighCutoffHz"]
			.NumericValue, Is.EqualTo(12000));
		Assert.Throws<ArgumentException>(() =>
			definition.SetStageDefaults(30, new[] {
				Pair("LowCutoffHz", FilterParameterValue.FromNumber(3000)),
				Pair("HighCutoffHz", FilterParameterValue.FromNumber(2000)),
			}));
		Assert.That(definition.GetStage(30).DefaultParameters["LowCutoffHz"]
			.NumericValue, Is.EqualTo(8000));
	}

	[Test]
	public void DefinitionMakesImmutableSnapshotsOfMutableInputCollections()
	{
		List<FilterStageDefinition> input = [
			new FilterStageDefinition(1, "begin", FilterKind.OnePole),
		];
		FilterDefinition definition = new((ObjectId)6U, "Immutable", input);
		input[0] = new FilterStageDefinition(2, "swapped", FilterKind.OnePole);
		input.Add(new FilterStageDefinition(3, "added", FilterKind.OnePole));
		Assert.That(definition.Stages.Select(s => s.Name), Is.EqualTo(new[] { "begin" }));

		Dictionary<string, FilterParameterValue> defaultInput = new() {
			["CutoffHz"] = FilterParameterValue.FromNumber(2500),
		};
		FilterStageDefinition stage = new(12, "a", FilterKind.BiquadLowPass,
			defaultInput);
		defaultInput["CutoffHz"] = FilterParameterValue.FromNumber(8000);
		Assert.That(stage.DefaultParameters["CutoffHz"].NumericValue, Is.EqualTo(2500));
		Assert.That(() => ((IDictionary)stage.DefaultParameters)["CutoffHz"] =
			FilterParameterValue.FromNumber(500), Throws.InstanceOf<NotSupportedException>());
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
