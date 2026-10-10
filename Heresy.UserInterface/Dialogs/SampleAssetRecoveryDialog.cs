using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.Core.Persistence;

namespace Heresy.UserInterface.Dialogs;

public enum SampleAssetRecoveryChoice
{
	Cancel,
	ChooseFile,
	SearchFolder,
}

/// <summary>
/// One unresolved asset at a time. No replacement is accepted by this dialog:
/// the loader validates bytes, manifest hash and decoder before continuing.
/// </summary>
public sealed class SampleAssetRecoveryDialog : Window
{
	public SampleAssetRecoveryDialog(SampleAssetRecoveryRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		Title = "Locate missing sample asset";
		Width = 650;
		Height = 310;
		MinWidth = 480;
		CanResize = true;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		Button chooseFile = new() { Content = "Choose _File...", MinWidth = 115 };
		chooseFile.Click += (_, _) => Close(SampleAssetRecoveryChoice.ChooseFile);
		Button searchFolder = new() { Content = "Search _Folder...", MinWidth = 125 };
		searchFolder.Click += (_, _) => Close(SampleAssetRecoveryChoice.SearchFolder);
		Button cancel = new() { Content = "_Cancel", MinWidth = 90 };
		cancel.Click += (_, _) => Close(SampleAssetRecoveryChoice.Cancel);
		DialogActionLayout.ConfigureButtons(chooseFile, cancel);

		StackPanel buttons = new()
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Spacing = 8,
		};
		buttons.Children.Add(chooseFile);
		buttons.Children.Add(searchFolder);
		buttons.Children.Add(cancel);

		StackPanel details = new() { Spacing = 9 };
		details.Children.Add(new TextBlock
		{
			Text = $"Sample: {request.SampleName} ({request.SampleId})",
			TextWrapping = TextWrapping.Wrap,
		});
		details.Children.Add(new TextBlock
		{
			Text = $"Original location: {request.OriginalFullPath}",
			TextWrapping = TextWrapping.Wrap,
		});
		details.Children.Add(new TextBlock
		{
			Text = $"Problem: {request.FailureMessage}",
			TextWrapping = TextWrapping.Wrap,
		});
		details.Children.Add(new TextBlock
		{
			Text = "Choose the original encoded audio file or search a folder for the same filename and SHA-256. The replacement must decode successfully. Cancel keeps your current song unchanged.",
			TextWrapping = TextWrapping.Wrap,
		});

		Content = DialogActionLayout.Create(
			new ScrollViewer { Content = details },
			buttons,
			new Thickness(18));
	}
}
