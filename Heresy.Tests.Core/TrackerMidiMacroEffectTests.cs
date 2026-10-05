using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerMidiMacroEffectTests
{
	[Test]
	public void DataGridTranslatesSFxAndZxxToRawMacroCommands()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerMidiMacroSelectPatternEffect(1));
		cell.Effects.Add(new TrackerMidiMacroPatternEffect(0x40));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToArray();

		Assert.That(
			commands,
			Does.Contain(new ApplyTrackerMidiMacroSelectCommand(1)));
		Assert.That(
			commands,
			Does.Contain(new ApplyTrackerMidiMacroCommand(0x40)));
	}

	[Test]
	public void DefaultZ40SetsFilterCutoffThroughSF0()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerMidiMacroPatternEffect(0x40));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetResonantFilterCutoffCommand>()
				.Single()
				.Cutoff,
			Is.EqualTo(0x40 / 127.0).Within(1e-12));
	}

	[Test]
	public void DefaultZ88SetsFilterResonanceThroughFixedMacro()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerMidiMacroPatternEffect(0x88));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetResonantFilterResonanceCommand>()
				.Single()
				.Resonance,
			Is.EqualTo(0x40 / 127.0).Within(1e-12));
	}

	[Test]
	public void SFxSelectionPersistsPerMappedPhysicalChannel()
	{
		TrackerMidiMacroConfiguration macros =
			TrackerMidiMacroConfiguration.CreateImpulseTrackerDefault();
		macros.SetParameterizedMacro(1, "F0 F0 01 z");

		DataPatternDefinition pattern = Pattern(2, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerMidiMacroSelectPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerMidiMacroPatternEffect(0x20));
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new TrackerMidiMacroPatternEffect(0x20));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(trackerMidiMacros: macros),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		NoteEvent channel0 = schedule.Single(noteEvent =>
			noteEvent.Target.Kind == ChannelTargetKind.Physical
				&& noteEvent.Target.PhysicalChannel == 0);
		NoteEvent channel1 = schedule.Single(noteEvent =>
			noteEvent.Target.Kind == ChannelTargetKind.Physical
				&& noteEvent.Target.PhysicalChannel == 1);

		Assert.That(
			channel0.Commands
				.OfType<SetResonantFilterResonanceCommand>()
				.Single()
				.Resonance,
			Is.EqualTo(0x20 / 127.0).Within(1e-12));
		Assert.That(
			channel1.Commands
				.OfType<SetResonantFilterCutoffCommand>()
				.Single()
				.Cutoff,
			Is.EqualTo(0x20 / 127.0).Within(1e-12));
	}

	[Test]
	public void UnrecognizedCustomMacroDoesNotMasqueradeAsFilterControl()
	{
		TrackerMidiMacroConfiguration macros =
			TrackerMidiMacroConfiguration.CreateImpulseTrackerDefault();
		macros.SetParameterizedMacro(1, "90 3C z");

		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerMidiMacroSelectPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerMidiMacroPatternEffect(0x40));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(trackerMidiMacros: macros),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.Any(command =>
					command is SetResonantFilterCutoffCommand
						or SetResonantFilterResonanceCommand),
			Is.False);
	}

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
