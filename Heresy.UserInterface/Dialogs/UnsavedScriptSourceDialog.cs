using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// This prompt is about source text not yet applied to the song. The Save
/// action commits the script object; the document's Save/Save As remains
/// a separate persistence action.
/// </summary>
public sealed class UnsavedScriptSourceDialog : Window
{
    public UnsavedScriptSourceDialog(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        Title = "Unsaved script source";
        Width = 540;
        Height = 220;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Button save = new() { Content = "_Save", MinWidth = 95 };
        save.Click += (_, _) => Close(UnsavedChangesChoice.Save);
        Button discard = new() { Content = "_Discard", MinWidth = 95 };
        discard.Click += (_, _) => Close(UnsavedChangesChoice.Discard);
        Button cancel = new() { Content = "_Cancel", MinWidth = 95 };
        cancel.Click += (_, _) => Close(UnsavedChangesChoice.Cancel);
        DialogActionLayout.ConfigureButtons(save, cancel);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        actions.Children.Add(save);
        actions.Children.Add(discard);
        actions.Children.Add(cancel);

        Content = DialogActionLayout.Create(
            new TextBlock
            {
                Text = $"Save unsaved script source changes to {sourceName} before leaving? " +
                    "Save applies the source to the song; Discard keeps the previously applied source.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
            actions, new Thickness(18), spacing: 16);
    }
}
