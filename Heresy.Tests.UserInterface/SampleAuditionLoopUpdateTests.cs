using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleAuditionLoopUpdateTests
{
	[AvaloniaTest]
	public void WaveformCommitRestartsNoteAfterPhysicalKeyWasReleased()
	{
		var (dialog, sample, keyboard, events) = Setup();
		SampleAuditionVoice note = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyUp(PhysicalKey.Z).Should().BeTrue();
		keyboard.KeyDown(PhysicalKey.NumPadMultiply,
			KeyModifiers.None, looping: false).Should().BeNull();
		keyboard.BaseOctave.Should().Be(5);
		keyboard.PressedKeyCount.Should().Be(0);
		keyboard.OngoingNotes.Should().ContainSingle().Which.Should().Be(note);

		SampleLoop updated = new(SampleLoopMode.Forward, 12, 40);
		Invoke(dialog, "CommitWaveformLoop", updated);
		sample.Loop.Should().Be(updated);
		events.Should().ContainSingle();
		events[0].Target.Should().Be(ChannelTarget.Virtual(note.VoiceId));
		events[0].Commands.Should().Equal(
			new NoteCutCommand(),
			new StartNoteCommand(sample.Id, PitchMultiplier: 1.0));

		Invoke(dialog, "CommitWaveformLoop",
			new SampleLoop(SampleLoopMode.Forward, 25, 45));
		events.Should().HaveCount(2,
			"a key release no longer retires its sample preview voice");
		events[1].Target.Should().Be(events[0].Target);
	}

	[AvaloniaTest]
	public void FocusLossCommitRestartsAllSoundingNotesWithoutApply()
	{
		var (dialog, sample, keyboard, events) = Setup();
		SampleAuditionVoice first = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyUp(PhysicalKey.Z);
		SampleAuditionVoice second = keyboard.KeyDown(
			PhysicalKey.C, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyUp(PhysicalKey.C);
		Field<ComboBox>(dialog, "_loopMode").SelectedItem =
			SampleLoopMode.Forward;
		Field<TextBox>(dialog, "_loopStart").Text = "7";
		Field<TextBox>(dialog, "_loopEnd").Text = "35";

		Invoke(dialog, "CommitLoopFields");

		sample.Loop.Should().Be(
			new SampleLoop(SampleLoopMode.Forward, 7, 35));
		events.Should().HaveCount(2);
		events[0].Target.Should().Be(ChannelTarget.Virtual(first.VoiceId));
		events[1].Target.Should().Be(ChannelTarget.Virtual(second.VoiceId));
		events[0].Commands.Should().Equal(
			new NoteCutCommand(),
			new StartNoteCommand(sample.Id,
				PitchMultiplier: first.PitchMultiplier));
		events[1].Commands.Should().Equal(
			new NoteCutCommand(),
			new StartNoteCommand(sample.Id,
				PitchMultiplier: second.PitchMultiplier));
	}

	[AvaloniaTest]
	public void NoLoopChangeDoesNotRetriggerPersistentPreview()
	{
		var (dialog, sample, keyboard, events) = Setup();
		keyboard.KeyDown(PhysicalKey.Z, KeyModifiers.None, looping: false);
		keyboard.KeyUp(PhysicalKey.Z);
		Invoke(dialog, "CommitWaveformLoop", sample.Loop);
		Invoke(dialog, "CommitLoopFields");
		events.Should().BeEmpty();
	}

	[AvaloniaTest]
	public void StoppedNotesAreNotRestartedBySubsequentLoopChanges()
	{
		var (dialog, sample, keyboard, events) = Setup();
		keyboard.KeyDown(PhysicalKey.Z, KeyModifiers.None, looping: false);
		keyboard.KeyUp(PhysicalKey.Z);
		keyboard.StopAll().Should().ContainSingle();
		Invoke(dialog, "CommitWaveformLoop",
			new SampleLoop(SampleLoopMode.Forward, 10, 20));
		events.Should().BeEmpty();
	}

	private static (SampleEditorDialog Dialog, SampleDefinition Sample,
		SampleAuditionKeyboard Keyboard, List<LivePlaybackEvent> Events) Setup()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		SampleDefinition sample = new(
			id, "Test Sample",
			new ExternalAssetReference(Path.GetFullPath(
				"test-sample-audition-does-not-exist.wav")));
		workspace.Document.Add(sample);
		List<LivePlaybackEvent> events = [];
		SampleEditorDialog dialog = new(
			workspace, sample,
			new PatternLiveAuditionActions(ev =>
			{
				events.Add(ev);
				return Task.CompletedTask;
			}));
		return (dialog, sample,
			Field<SampleAuditionKeyboard>(dialog, "_auditionKeyboard"),
			events);
	}

	private static T Field<T>(SampleEditorDialog dialog, string name)
		=> (T)typeof(SampleEditorDialog)
			.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(dialog)!;

	private static void Invoke(SampleEditorDialog dialog,
		string method, params object[] arguments)
		=> typeof(SampleEditorDialog)
			.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(dialog, arguments);
}
