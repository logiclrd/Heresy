using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NativePatternEffectEditorTests
{
	public static IEnumerable<TestCaseData> NativeEffects()
	{
		yield return new TestCaseData(new SetTempoPatternEffect(24.5));
		yield return new TestCaseData(new SetSpeedPatternEffect(6));
		yield return new TestCaseData(new SetNoteVolumePatternEffect(0.75));
		yield return new TestCaseData(new SetOverallChannelVolumePatternEffect(0.5));
		yield return new TestCaseData(new SetPlaybackFrequencyPatternEffect(440.0));
		yield return new TestCaseData(
			new SetPlaybackOffsetPatternEffect(TimeSpan.FromSeconds(1.25)));
		yield return new TestCaseData(
			new SetResonantFilterPatternEffect(0.4, 0.7));
		yield return new TestCaseData(new PitchSlidePatternEffect(-12.5));
		yield return new TestCaseData(new NoteVolumeSlidePatternEffect(3.25));
		yield return new TestCaseData(
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.Vibrato,
				7));
		yield return new TestCaseData(
			new TrackerVolumeColumnPanningPatternEffect(48));
		yield return new TestCaseData(
			new ParametricPatternEffect(
				ParametricEffectParameter.OverallChannelVolume,
				TimeSpan.FromMilliseconds(125),
				"Math.Sin(elapsedSeconds) * 0.2 + rowTime * 0.0",
				commitOnExpire: true));
	}

	[TestCaseSource(nameof(NativeEffects))]
	public void EveryNativeEffectCanRoundTripThroughGenericEditModel(
		PatternEffect effect)
	{
		NativePatternEffectEditModel model =
			NativePatternEffectEditor.Describe(
				effect,
				CultureInfo.InvariantCulture);

		model.Title.Should().NotBeNullOrWhiteSpace();
		model.Fields.Should().NotBeEmpty();

		Dictionary<string, string> values = [];
		foreach (NativePatternEffectField field in model.Fields)
			values.Add(field.Key, field.Value);

		PatternEffect rebuilt =
			NativePatternEffectEditor.Rebuild(
				effect,
				values,
				CultureInfo.InvariantCulture);

		rebuilt.Should().Be(effect);
		rebuilt.GetType().Should().Be(effect.GetType());
	}

	[Test]
	public void NativeFilterTextEditorUsesDefinitionDescriptorsAndShowsErrorsBeforeSave()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		document.Add(new FilterDefinition(filterId, "EQ", [
			new FilterStageDefinition(2, "stage2", FilterKind.BiquadLowPass),
		]));
		var original = new SetChannelFilterAssignmentsPatternEffect(
			"stage2.CutoffHz = 2500", filterId);
		var fields = NativePatternEffectEditor.Describe(
			original, CultureInfo.InvariantCulture).Fields;
		Assert.That(fields.Single(f => f.Key == "assignments").Multiline, Is.True);
		Dictionary<string, string> inputs = fields.ToDictionary(f => f.Key,
			f => f.Value);
		Assert.That(NativePatternEffectEditor.Rebuild(original, inputs,
			CultureInfo.InvariantCulture, document), Is.EqualTo(original));
		inputs["assignments"] = """
			stage2.CutoffHz = 2000
			stage2.CutoffHz = 2500
			""";
		Assert.That(() => NativePatternEffectEditor.Rebuild(original, inputs,
			CultureInfo.InvariantCulture, document),
			Throws.ArgumentException.With.Message.Contains("Duplicate"));
		inputs["assignments"] = "stage2.Unsupported = 7";
		Assert.That(() => NativePatternEffectEditor.Rebuild(original, inputs,
			CultureInfo.InvariantCulture, document), Throws.ArgumentException);
	}

	[Test]
	public void NativeFilterSelectionEditorRejectsMissingOrWrongTypeDefinition()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		document.Add(new FilterDefinition(filterId, "EQ"));
		var selected = new SetChannelFilterPatternEffect(filterId);
		var fields = NativePatternEffectEditor.Describe(selected).Fields;
		var input = fields.ToDictionary(f => f.Key, f => f.Value);
		Assert.That(NativePatternEffectEditor.Rebuild(selected, input,
			CultureInfo.InvariantCulture, document), Is.EqualTo(selected));
		input["filterId"] = "999999";
		Assert.That(() => NativePatternEffectEditor.Rebuild(selected, input,
			CultureInfo.InvariantCulture, document), Throws.ArgumentException);
		input["filterId"] = "";
		Assert.That(NativePatternEffectEditor.Rebuild(selected, input,
			CultureInfo.InvariantCulture, document),
			Is.EqualTo(new SetChannelFilterPatternEffect()));
	}

	[Test]
	public void EnumFieldExposesVolumeColumnKindsAsChoices()
	{
		NativePatternEffectEditModel model =
			NativePatternEffectEditor.Describe(
				new TrackerVolumeColumnPatternEffect(
					TrackerVolumeColumnEffectKind.TonePortamento,
					5),
				CultureInfo.InvariantCulture);

		NativePatternEffectField kind = model.Fields[0];
		kind.Key.Should().Be("kind");
		kind.Choices.Should().NotBeNull();
		kind.Choices!.Should().Contain(
			nameof(TrackerVolumeColumnEffectKind.TonePortamento));
		kind.Value.Should().Be(
			nameof(TrackerVolumeColumnEffectKind.TonePortamento));
	}

	[Test]
	public void ParametricEditorRejectsInvalidOrUnsafeExpressionsBeforeMutation()
	{
		ParametricPatternEffect original = new(
			ParametricEffectParameter.GlobalVolume,
			TimeSpan.FromMilliseconds(100), "-0.5");
		NativePatternEffectEditModel model =
			NativePatternEffectEditor.Describe(original, CultureInfo.InvariantCulture);
		Dictionary<string, string> input = [];
		foreach (NativePatternEffectField field in model.Fields)
			input[field.Key] = field.Value;
		input["expression"] = "System.Environment.TickCount";
		var unsafeAction = () => NativePatternEffectEditor.Rebuild(
			original, input, CultureInfo.InvariantCulture);
		unsafeAction.Should().Throw<ArgumentException>();
		input["expression"] = "-0.2";
		input["duration"] = "-1";
		var badDuration = () => NativePatternEffectEditor.Rebuild(
			original, input, CultureInfo.InvariantCulture);
		badDuration.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void TrackerStyleEffectCannotUseNativeEditor()
	{
		PatternEffect tracker =
			new TonePortamentoPatternEffect(0x15);

		NativePatternEffectEditor.CanEdit(tracker).Should().BeFalse();

		var action = () =>
			NativePatternEffectEditor.Describe(
				tracker,
				CultureInfo.InvariantCulture);

		action.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void RebuildUsesCoreConstructorValidation()
	{
		SetPlaybackFrequencyPatternEffect original = new(440.0);
		NativePatternEffectEditModel model =
			NativePatternEffectEditor.Describe(
				original,
				CultureInfo.InvariantCulture);
		Dictionary<string, string> values =
			new()
			{
				["frequency"] = "0",
			};

		var action = () =>
			NativePatternEffectEditor.Rebuild(
				original,
				values,
				CultureInfo.InvariantCulture);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void RebuildRejectsMalformedScalarBeforeMutation()
	{
		SetTempoPatternEffect original = new(24.0);
		Dictionary<string, string> values =
			new()
			{
				["ticksPerDiachron"] = "definitely-not-a-number",
			};

		var action = () =>
			NativePatternEffectEditor.Rebuild(
				original,
				values,
				CultureInfo.InvariantCulture);

		action.Should().Throw<ArgumentException>();
	}

	[Test]
	public void ReplaceSelectedEffectMarksAudioAndPreservesStackPosition()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x15));
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440.0));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);
		cursor.MoveRight(2, 1, cell);
		cursor.MoveRight(2, 1, cell);
		cursor.ExpandedEffectIndex.Should().Be(1);
		uint audioRevision = workspace.Document.AudioRevision;

		bool changed =
			PatternEffectStackEditor.ReplaceSelected(
				workspace,
				pattern,
				cursor,
				new SetPlaybackFrequencyPatternEffect(880.0));

		changed.Should().BeTrue();
		cell.Effects.Should().Equal(
			new TonePortamentoPatternEffect(0x15),
			new SetPlaybackFrequencyPatternEffect(880.0),
			new VibratoPatternEffect(0x22));
		cursor.ExpandedEffectIndex.Should().Be(1);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void ReplacingWithEqualEffectIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(6));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		bool changed =
			PatternEffectStackEditor.ReplaceSelected(
				workspace,
				pattern,
				cursor,
				new SetSpeedPatternEffect(6));

		changed.Should().BeFalse();
		workspace.Document.DocumentRevision.Should().Be(documentRevision);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}
}
