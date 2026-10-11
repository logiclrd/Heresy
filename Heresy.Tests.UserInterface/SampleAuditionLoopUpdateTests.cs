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
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleAuditionLoopUpdateTests
{
	[AvaloniaTest]
	public void WaveformCommitRestartsExactHeldSampleNoteAfterLoopChanges()
	{
		var (dialog, sample, keyboard, events) = Setup();
		StartFmSynthAuditionNote held =
			keyboard.KeyDown(PhysicalKey.Z, KeyModifiers.None)!;
		keyboard.KeyDown(PhysicalKey.NumPadMultiply, KeyModifiers.None);
		keyboard.BaseOctave.Should().Be(5);

		SampleLoop updated = new(SampleLoopMode.Forward, 12, 40);
		Invoke(dialog, "CommitWaveformLoop", updated);
		sample.Loop.Should().Be(updated);
		events.Should().ContainSingle();
		events[0].Target.Should().Be(ChannelTarget.Virtual(held.VoiceId));
		events[0].Commands.Should().Equal(
			new NoteCutCommand(),
			new StartNoteCommand(sample.Id, PitchMultiplier: 1.0));

		keyboard.KeyUp(PhysicalKey.Z);
		Invoke(dialog, "CommitWaveformLoop",
			new SampleLoop(SampleLoopMode.Forward, 25, 45));
		events.Should().ContainSingle(
			"changing a loop cannot re-trigger keys which have been released");
	}

	[AvaloniaTest]
	public void FocusLossCommitUpdatesWaveformAndRestartsHeldSampleWithoutApply()
	{
		var (dialog, sample, keyboard, events) = Setup();
		StartFmSynthAuditionNote held =
			keyboard.KeyDown(PhysicalKey.X, KeyModifiers.None)!;
		Field<ComboBox>(dialog, "_loopMode").SelectedItem =
			SampleLoopMode.Forward;
		Field<TextBox>(dialog, "_loopStart").Text = "7";
		Field<TextBox>(dialog, "_loopEnd").Text = "35";

		Invoke(dialog, "CommitLoopFields");

		sample.Loop.Should().Be(
			new SampleLoop(SampleLoopMode.Forward, 7, 35));
		events.Should().ContainSingle();
		events[0].Target.Should().Be(ChannelTarget.Virtual(held.VoiceId));
		events[0].Commands.Should().Equal(
			new NoteCutCommand(),
			new StartNoteCommand(sample.Id,
				PitchMultiplier: held.PitchMultiplier));
	}

	[AvaloniaTest]
	public void NoLoopChangeDoesNotRetriggerHeldPreview()
	{
		var (dialog, sample, keyboard, events) = Setup();
		keyboard.KeyDown(PhysicalKey.Z, KeyModifiers.None);
		Invoke(dialog, "CommitWaveformLoop", sample.Loop);
		Invoke(dialog, "CommitLoopFields");
		events.Should().BeEmpty();
	}

	private static (SampleEditorDialog Dialog, SampleDefinition Sample,
		FmSynthAuditionKeyboard Keyboard, List<LivePlaybackEvent> Events) Setup()
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
			Field<FmSynthAuditionKeyboard>(dialog, "_auditionKeyboard"),
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
