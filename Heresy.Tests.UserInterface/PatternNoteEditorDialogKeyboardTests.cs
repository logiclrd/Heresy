using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Dialogs;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternNoteEditorDialogKeyboardTests
{
	[AvaloniaTest]
	public void EnterOnFocusedMixdownAppliesWithoutChangingCheckbox()
	{
		foreach (bool initialMixdown in new[] { false, true })
		{
			StartPatternNote original = new(
				pitchMultiplier: 1.25,
				playbackSpeedMultiplier: 0.75,
				mixdown: initialMixdown);
			using Modal modal = new(original);
			Assert.That(modal.Mixdown.Focus(), Is.True);
			Assert.That(modal.Mixdown.IsChecked, Is.EqualTo(initialMixdown));

			KeyEventArgs enter = Press(modal.Mixdown, Key.Enter);
			Assert.Multiple(() =>
			{
				Assert.That(enter.Handled, Is.True,
					"focused checkbox must consume Enter before its toggle handler");
				Assert.That(modal.Mixdown.IsChecked, Is.EqualTo(initialMixdown),
					"Enter must not toggle the Mixdown setting");
				Assert.That(modal.Result.IsCompletedSuccessfully, Is.True,
					"Enter should invoke the dialog's Apply action");
			});

			StartPatternNote saved =
				(StartPatternNote)modal.Result.Result!.Note!;
			Assert.Multiple(() =>
			{
				Assert.That(saved.Mixdown, Is.EqualTo(initialMixdown));
				Assert.That(saved.PitchMultiplier, Is.EqualTo(1.25));
				Assert.That(saved.PlaybackSpeedMultiplier, Is.EqualTo(0.75));
				Assert.That(original.Mixdown, Is.EqualTo(initialMixdown),
					"the dialog returns a value; it must not mutate the original note");
			});
		}
	}

	[AvaloniaTest]
	public void SpaceOnFocusedMixdownTogglesOnceWithoutApplying()
	{
		using Modal modal = new(new StartPatternNote(mixdown: false));
		Assert.That(modal.Mixdown.Focus(), Is.True);

		Press(modal.Mixdown, Key.Space);
		Release(modal.Mixdown, Key.Space);
		Assert.Multiple(() =>
		{
			Assert.That(modal.Mixdown.IsChecked, Is.True,
				"Space retains normal checkbox semantics");
			Assert.That(modal.Result.IsCompleted, Is.False,
				"Space must not apply or dismiss the dialog");
		});

		Press(modal.Mixdown, Key.Space);
		Release(modal.Mixdown, Key.Space);
		Assert.Multiple(() =>
		{
			Assert.That(modal.Mixdown.IsChecked, Is.False,
					"each Space press should toggle once");
			Assert.That(modal.Result.IsCompleted, Is.False);
		});

		Press(modal.Mixdown, Key.Enter);
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(((StartPatternNote)modal.Result.Result!.Note!).Mixdown,
			Is.False);
	}

	[AvaloniaTest]
	public void InvalidDraftEnterOnMixdownLeavesDialogOpenUntilCorrected()
	{
		using Modal modal = new(new StartPatternNote(mixdown: true));
		modal.Pitch.Text = "invalid number";
		Assert.That(modal.Mixdown.Focus(), Is.True);

		KeyEventArgs invalid = Press(modal.Mixdown, Key.Enter);
		Assert.Multiple(() =>
		{
			Assert.That(invalid.Handled, Is.True);
			Assert.That(modal.Result.IsCompleted, Is.False);
			Assert.That(modal.Dialog.IsVisible, Is.True);
			Assert.That(modal.Message.Text, Does.Contain("valid number"));
			Assert.That(modal.Mixdown.IsChecked, Is.True);
		});

		modal.Pitch.Text = "2.5";
		KeyEventArgs valid = Press(modal.Mixdown, Key.Enter);
		Assert.That(valid.Handled, Is.True);
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		StartPatternNote accepted =
			(StartPatternNote)modal.Result.Result!.Note!;
		Assert.That(accepted.PitchMultiplier, Is.EqualTo(2.5));
		Assert.That(accepted.Mixdown, Is.True);
	}

	[AvaloniaTest]
	public void ApplyRemainsDefaultAndCancelRetainsEscapeRole()
	{
		using Modal modal = new(new StartPatternNote());
		Assert.That(modal.ApplyButton.IsDefault, Is.True);
		Assert.That(modal.CancelButton.IsCancel, Is.True);
		modal.ApplyButton.RaiseEvent(
			new RoutedEventArgs(Button.ClickEvent));
		Assert.That(modal.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(modal.Result.Result!.Note,
			Is.TypeOf<StartPatternNote>());

		using Modal cancelled = new(new StartPatternNote(mixdown: true));
		cancelled.CancelButton.RaiseEvent(
			new RoutedEventArgs(Button.ClickEvent));
		Assert.That(cancelled.Result.IsCompletedSuccessfully, Is.True);
		Assert.That(cancelled.Result.Result, Is.Null);
	}

	private static KeyEventArgs Press(Control target, Key key)
	{
		KeyEventArgs e = new()
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = key,
			KeyModifiers = KeyModifiers.None,
		};
		target.RaiseEvent(e);
		return e;
	}

	private static void Release(Control target, Key key) =>
		target.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = key,
			KeyModifiers = KeyModifiers.None,
		});

	private sealed class Modal : IDisposable
	{
		private readonly Window _owner;
		public PatternNoteEditorDialog Dialog { get; }
		public Task<PatternNoteEditResult?> Result { get; }

		public Modal(PatternNoteEntry? note)
		{
			_owner = new Window { Width = 800, Height = 600 };
			_owner.Show();
			Dialog = new PatternNoteEditorDialog(
				new SongDocument(), row: 0, channel: 0, note);
			Result = Dialog.ShowDialog<PatternNoteEditResult?>(_owner);
		}

		public CheckBox Mixdown => Field<CheckBox>("_mixdown");
		public TextBox Pitch => Field<TextBox>("_pitch");
		public TextBlock Message => Field<TextBlock>("_message");

		public Button ApplyButton => Button("Apply");
		public Button CancelButton => Button("Cancel");

		private Button Button(string text) =>
			((Grid)Dialog.Content!).Children
				.OfType<StackPanel>().Last()
				.Children.OfType<Button>()
				.Single(x => Equals(x.Content, text));

		private T Field<T>(string name) =>
			(T)typeof(PatternNoteEditorDialog)
				.GetField(name,
					BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(Dialog)!;

		public void Dispose()
		{
			if (Dialog.IsVisible)
				Dialog.Close();
			_owner.Close();
		}
	}
}
