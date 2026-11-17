using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class F2RenameShortcutTests
{
	private const BindingFlags PrivateInstance =
		BindingFlags.NonPublic | BindingFlags.Instance;

	private static T Field<T>(object owner, string name)
		=> (T)owner.GetType().GetField(name, PrivateInstance)!
			.GetValue(owner)!;

	private static KeyEventArgs KeyEvent(
		Key key, KeyModifiers modifiers = KeyModifiers.None,
		bool handled = false)
		=> new()
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = key,
			KeyModifiers = modifiers,
			Handled = handled,
		};

	[Test]
	public void OnlyUnhandledUnmodifiedF2RequestsRename()
	{
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F2, KeyModifiers.None, false), Is.True);
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F2, KeyModifiers.Control, false), Is.False);
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F2, KeyModifiers.Shift, false), Is.False);
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F2, KeyModifiers.Alt, false), Is.False);
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F2, KeyModifiers.None, true), Is.False);
		Assert.That(RenameKeyboardShortcut.IsRequested(
			Key.F3, KeyModifiers.None, false), Is.False);
	}

	[AvaloniaTest]
	public async Task F2InPatternGridReusesInlineTitleEditorAndItsCancelCommitSemantics()
	{
		DocumentWorkspace work = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			work, "Verse", rowCount: 4, channelCount: 1);
		PatternEditorControl editor = new(new Window(), work, pattern,
			() => { }, _ => { });
		TextBox titleEditor = Field<TextBox>(editor, "_titleEditor");
		uint revision = work.Document.DocumentRevision;
		uint audio = work.Document.AudioRevision;

		KeyEventArgs ctrlF2 = KeyEvent(Key.F2, KeyModifiers.Control);
		await PressGrid(editor, ctrlF2);
		Assert.That(ctrlF2.Handled, Is.False);
		Assert.That(titleEditor.IsVisible, Is.False);
		KeyEventArgs handledF2 = KeyEvent(Key.F2, handled: true);
		await PressGrid(editor, handledF2);
		Assert.That(titleEditor.IsVisible, Is.False);

		KeyEventArgs first = KeyEvent(Key.F2);
		await PressGrid(editor, first);
		Assert.That(first.Handled, Is.True);
		Assert.That(titleEditor.IsVisible, Is.True);
		Assert.That(titleEditor.Text, Is.EqualTo("Verse"));
		titleEditor.Text = "Discard";
		var escape = KeyEvent(Key.Escape);
		typeof(PatternEditorControl).GetMethod("OnTitleEditorKeyDown", PrivateInstance)!
			.Invoke(editor, new object?[] { titleEditor, escape });
		Assert.That(escape.Handled, Is.True);
		Assert.That(pattern.Name, Is.EqualTo("Verse"));
		Assert.That(titleEditor.IsVisible, Is.False);
		Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision));

		KeyEventArgs second = KeyEvent(Key.F2);
		await PressGrid(editor, second);
		titleEditor.Text = "Verse II";
		var enter = KeyEvent(Key.Enter);
		typeof(PatternEditorControl).GetMethod("OnTitleEditorKeyDown", PrivateInstance)!
			.Invoke(editor, new object?[] { titleEditor, enter });
		Assert.That(enter.Handled, Is.True);
		Assert.That(pattern.Name, Is.EqualTo("Verse II"));
		Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision + 1));
		Assert.That(work.Document.AudioRevision, Is.EqualTo(audio));
		Assert.That(work.Document.GetSectionRoot(SongTreeSection.Patterns)
			.Children.OfType<SongTreeObject>().Single().Name,
			Is.EqualTo("Verse II"));
	}

	[AvaloniaTest]
	public async Task TreeF2ReusesContextRenameDialogWithoutInterceptingModifiedKeys()
	{
		DocumentWorkspace work = new();
		SongTreeFolder folder = SongTreeEditor.CreateFolder(
			work.Document, work.Document.GetSectionRoot(SongTreeSection.Patterns),
			"Drums");
		MainWindow window = new(work);
		window.Show();
		try
		{
			window.UpdateLayout();
			Dictionary<SongTreeSection, TreeView> trees =
				Field<Dictionary<SongTreeSection, TreeView>>(window, "_trees");
			TreeView tree = trees[SongTreeSection.Patterns];
			TreeViewItem control = ((IEnumerable<object>)tree.ItemsSource!)
				.OfType<TreeViewItem>().Single();
			tree.SelectedItem = control;
			uint revision = work.Document.DocumentRevision;
			uint audio = work.Document.AudioRevision;

			KeyEventArgs controlF2 = KeyEvent(Key.F2, KeyModifiers.Control);
			tree.RaiseEvent(controlF2);
			Assert.That(controlF2.Handled, Is.False);
			Assert.That(Field<TextPromptDialog?>(window, "_activeRenamePrompt"),
				Is.Null);

			KeyEventArgs f2 = KeyEvent(Key.F2);
			tree.RaiseEvent(f2);
			Assert.That(f2.Handled, Is.True);
			TextPromptDialog prompt =
				Field<TextPromptDialog?>(window, "_activeRenamePrompt")!;
			Assert.That(prompt.Title, Is.EqualTo("Rename folder"));
			Assert.That(Field<TextBox>(prompt, "_textBox").Text,
				Is.EqualTo("Drums"));
			Assert.That(work.Document.DocumentRevision,
				Is.EqualTo(revision), "opening Rename must not edit the song");

			// Cancel the very same prompt shown by the context menu.
			prompt.Close((string?)null);
			await Task.Yield();
			Assert.That(folder.Name, Is.EqualTo("Drums"));
			Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision));
			Assert.That(work.Document.AudioRevision, Is.EqualTo(audio));
		}
		finally
		{
			work.New();
			window.Close();
		}
	}

	private static async Task PressGrid(
		PatternEditorControl editor, KeyEventArgs e)
	{
		Task task = (Task)typeof(PatternEditorControl)
			.GetMethod("OnCellKeyDownAsync", PrivateInstance)!
			.Invoke(editor, [0, 0, e])!;
		await task;
	}
}
