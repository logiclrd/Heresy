using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NativePatternEffectDialogKeyboardTests
{
	[AvaloniaTest]
	public void ControlEnterAndApplyButtonReturnTheSameEditedEffect()
	{
		using Modal keyboard = new(new SetSpeedPatternEffect(6));
		TextBox input = keyboard.Input("ticksPerRow");
		input.Text = "9";
		Assert.That(input.Focus(), Is.True);

		KeyEventArgs pressed = Press(input, Key.Enter, KeyModifiers.Control);
		Assert.That(pressed.Handled, Is.True);
		Assert.That(keyboard.Result.IsCompletedSuccessfully, Is.True);

		using Modal clicked = new(new SetSpeedPatternEffect(6));
		clicked.Input("ticksPerRow").Text = "9";
		clicked.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(clicked.Result.IsCompletedSuccessfully, Is.True);

		PatternEffect effect = keyboard.Result.Result!.Effect;
		Assert.That(effect, Is.EqualTo(clicked.Result.Result!.Effect));
		Assert.That(effect, Is.EqualTo(new SetSpeedPatternEffect(9)));

		// The dialog returns a value; only the caller's stack transaction
		// changes authoring state, and it does so once.
		DocumentWorkspace work = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			work, "Edit", rowCount: 1, channelCount: 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(6));
		uint before = work.Document.AudioRevision;
		Assert.That(work.Document.AudioRevision, Is.EqualTo(before));
		Assert.That(PatternEffectStackEditor.ReplaceSelected(work, pattern,
			new PatternEffectCursor(0, 0, PatternCellField.EffectCommand),
			effect), Is.True);
		Assert.That(cell.Effects.Single(), Is.EqualTo(effect));
		Assert.That(work.Document.AudioRevision, Is.EqualTo(before + 1));
	}

	[AvaloniaTest]
	public void InvalidDraftStaysOpenWithDiagnosticUntilCorrected()
	{
		using Modal modal = new(new SetPlaybackFrequencyPatternEffect(440));
		TextBox input = modal.Input("frequency");
		input.Text = "not-a-number";
		KeyEventArgs invalid = Press(input, Key.Enter, KeyModifiers.Control);
		Assert.That(invalid.Handled, Is.True);
		Assert.That(modal.Result.IsCompleted, Is.False);
		Assert.That(modal.Dialog.IsVisible, Is.True);
		Assert.That(modal.Message.Text, Does.Contain("valid number"));
		Assert.That(input.Text, Is.EqualTo("not-a-number"));

		input.Text = "880";
		KeyEventArgs valid = Press(input, Key.Enter, KeyModifiers.Control);
		Assert.That(valid.Handled, Is.True);
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(modal.Result.Result!.Effect,
			Is.EqualTo(new SetPlaybackFrequencyPatternEffect(880)));
	}

	[AvaloniaTest]
	public void ControlEnterInCreationModeUsesInsertButtonSemantics()
	{
		using Modal keyboard = new();
		NativePatternEffectChoice speed =
			((IEnumerable<NativePatternEffectChoice>)keyboard.TypeSelector.ItemsSource!)
			.Single(choice => choice.Kind == NativePatternEffectKind.Speed);
		keyboard.TypeSelector.SelectedItem = speed;
		keyboard.Input("ticksPerRow").Text = "8";
		Assert.That(keyboard.Button("Insert").IsDefault, Is.True);
		KeyEventArgs pressed = Press(keyboard.Input("ticksPerRow"),
			Key.Enter, KeyModifiers.Control);
		Assert.That(pressed.Handled, Is.True);
		Assert.That(keyboard.Result.IsCompletedSuccessfully, Is.True);

		using Modal clicked = new();
		clicked.TypeSelector.SelectedItem =
			((IEnumerable<NativePatternEffectChoice>)clicked.TypeSelector.ItemsSource!)
			.Single(choice => choice.Kind == NativePatternEffectKind.Speed);
		clicked.Input("ticksPerRow").Text = "8";
		clicked.Button("Insert").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(clicked.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(keyboard.Result.Result!.Effect,
			Is.EqualTo(clicked.Result.Result!.Effect));
		Assert.That(keyboard.Result.Result.Effect,
			Is.EqualTo(new SetSpeedPatternEffect(8)));
	}

	[AvaloniaTest]
	public void ControlEnterFromMultilineFilterEditorValidatesWithoutInsertingNewline()
	{
		SongDocument song = new();
		ObjectId id = song.AllocateObjectId();
		song.Add(new FilterDefinition(id, "EQ", [
			new FilterStageDefinition(2, "stage2", FilterKind.BiquadLowPass),
		]));
		using Modal modal = new(
			new SetNoteFilterAssignmentsPatternEffect(
				"stage2.CutoffHz = 2500", id), song);
		TextBox assignments = modal.Input("assignments");
		Assert.That(assignments.AcceptsReturn, Is.True);
		string authored = "stage2.CutoffHz = 1800";
		assignments.Text = authored;
		Assert.That(assignments.Focus(), Is.True);

		KeyEventArgs pressed = Press(assignments, Key.Enter,
			KeyModifiers.Control);
		Assert.That(pressed.Handled, Is.True);
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(assignments.Text, Is.EqualTo(authored));
		Assert.That(modal.Result.Result!.Effect,
			Is.EqualTo(new SetNoteFilterAssignmentsPatternEffect(authored, id)));
	}

	[AvaloniaTest]
	public void FilterInvalidDraftWarnsFirstAndOnlyUnchangedSecondApplyCommits()
	{
		SongDocument song = new();
		ObjectId id = song.AllocateObjectId();
		song.Add(new FilterDefinition(id, "EQ", [
			new FilterStageDefinition(2, "stage2", FilterKind.BiquadLowPass),
		]));
		using Modal modal = new(new SetNoteFilterAssignmentsPatternEffect(
			"stage2.CutoffHz = 1200", id), song);
		TextBox assignments = modal.Input("assignments");
		assignments.Text = "stage2.CutoffHz = 1500\nstage2.Invalid = 12";
		modal.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Multiple(() =>
		{
			Assert.That(modal.Result.IsCompleted, Is.False);
			Assert.That(modal.Message.Text, Does.Contain("warning").IgnoreCase);
			Assert.That(modal.Message.Text, Does.Contain("again"));
		});
		// Editing after the warning is a new draft requiring another warning.
		assignments.Text = "stage2.CutoffHz = 1700\nstage2.Invalid = 12";
		modal.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(modal.Result.IsCompleted, Is.False);
		modal.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(modal.Result.Result!.Effect,
			Is.EqualTo(new SetNoteFilterAssignmentsPatternEffect(
				"stage2.CutoffHz = 1700\nstage2.Invalid = 12", id)));
	}

	[AvaloniaTest]
	public void InvalidFilterSelectionCannotBeBypassedByRepeatedApply()
	{
		SongDocument song = new();
		using Modal modal = new(new SetChannelFilterAssignmentsPatternEffect(
			"stage2.CutoffHz = 1500", (ObjectId)999U), song);
		modal.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		modal.Button("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(modal.Result.IsCompleted, Is.False);
		Assert.That(modal.Message.Text, Does.Contain("existing shared Filter"));
	}

	[AvaloniaTest]
	public void OtherModifiersHandledKeysAndCancellationDoNotApplyShortcut()
	{
		using Modal modal = new(new SetSpeedPatternEffect(6));
		TextBox input = modal.Input("ticksPerRow");
		input.Text = "10";
		// Test this dialog's explicit shortcut filter in isolation:
		// Avalonia's existing IsDefault button may independently process
		// other Enter combinations in the complete routed keyboard pipeline.
		foreach (KeyModifiers modifiers in new[] {
			KeyModifiers.None, KeyModifiers.Shift, KeyModifiers.Alt,
			KeyModifiers.Control | KeyModifiers.Shift,
			KeyModifiers.Control | KeyModifiers.Alt,
		})
		{
			KeyEventArgs other = new()
			{
				RoutedEvent = InputElement.KeyDownEvent,
				Key = Key.Enter,
				KeyModifiers = modifiers,
			};
			InvokeShortcutHandler(modal.Dialog, other);
			Assert.That(other.Handled, Is.False);
			Assert.That(modal.Result.IsCompleted, Is.False);
		}
		KeyEventArgs prior = new()
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.Enter,
			KeyModifiers = KeyModifiers.Control,
			Handled = true,
		};
		InvokeShortcutHandler(modal.Dialog, prior);
		Assert.That(prior.Handled, Is.True);
		Assert.That(modal.Result.IsCompleted, Is.False);
		Assert.That(modal.Button("Cancel").IsCancel, Is.True);

		// The ordinary Cancel path still closes without a created/edited effect.
		modal.Button("Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(modal.Result.Result, Is.Null);
	}

	private static void InvokeShortcutHandler(
		NativePatternEffectEditorDialog dialog, KeyEventArgs e) =>
		typeof(NativePatternEffectEditorDialog)
			.GetMethod("OnDialogKeyDown",
				BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(dialog, [dialog, e]);

	private static KeyEventArgs Press(Control target, Key key,
		KeyModifiers modifiers = KeyModifiers.None, bool alreadyHandled = false)
	{
		KeyEventArgs e = new()
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = key,
			KeyModifiers = modifiers,
			Handled = alreadyHandled,
		};
		target.RaiseEvent(e);
		return e;
	}

	private sealed class Modal : IDisposable
	{
		private readonly Window _owner;
		public NativePatternEffectEditorDialog Dialog { get; }
		public Task<NativePatternEffectEditResult?> Result { get; }

		public Modal(PatternEffect? effect = null, SongDocument? song = null)
		{
			_owner = new Window { Width = 800, Height = 600 };
			_owner.Show();
			Dialog = effect is null
				? new NativePatternEffectEditorDialog(song)
				: new NativePatternEffectEditorDialog(effect, song);
			Result = Dialog.ShowDialog<NativePatternEffectEditResult?>(_owner);
		}

		public TextBox Input(string key) =>
			(TextBox)Field<Dictionary<string, Control>>("_inputs")[key];

		public TextBlock Message => Field<TextBlock>("_message");
		public ComboBox TypeSelector => Field<ComboBox>("_typeSelector");

		public Button Button(string label) =>
			((Grid)Dialog.Content!).Children.OfType<StackPanel>()
				.Last().Children.OfType<Button>()
				.Single(button => Equals(button.Content, label));

		private T Field<T>(string name) =>
			(T)typeof(NativePatternEffectEditorDialog)
				.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(Dialog)!;

		public void Dispose()
		{
			if (Dialog.IsVisible)
				Dialog.Close();
			_owner.Close();
		}
	}
}
