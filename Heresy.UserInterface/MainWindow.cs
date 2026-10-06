using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface;

public sealed class MainWindow : Window
{
	private static readonly FilePickerFileType SongFileType =
		new("Heresy song")
		{
			Patterns = new[] { "*.json" },
		};

	private readonly DocumentWorkspace _workspace;
	private readonly TreeView _tree;
	private readonly TextBlock _editorTitle;
	private readonly TextBlock _editorDetails;
	private readonly TextBlock _editorHint;
	private readonly TextBlock _status;

	public MainWindow()
		: this(new DocumentWorkspace())
	{
	}

	internal MainWindow(DocumentWorkspace workspace)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));

		Width = 1200;
		Height = 760;
		MinWidth = 760;
		MinHeight = 480;

		_tree = new TreeView();

		_editorTitle =
			new TextBlock
			{
				FontSize = 24,
				FontWeight = FontWeight.SemiBold,
			};
		_editorDetails =
			new TextBlock
			{
				Margin = new Thickness(0, 10, 0, 0),
			};
		_editorHint =
			new TextBlock
			{
				Margin = new Thickness(0, 18, 0, 0),
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 720,
			};
		_status =
			new TextBlock
			{
				VerticalAlignment = VerticalAlignment.Center,
			};

		Content = BuildShell();
		RefreshDocumentView("New song");
	}

	private Control BuildShell()
	{
		DockPanel root = new();

		Menu menu = BuildMenu();
		DockPanel.SetDock(menu, Dock.Top);
		root.Children.Add(menu);

		Border statusBar =
			new()
			{
				BorderThickness = new Thickness(0, 1, 0, 0),
				BorderBrush = Brushes.Gray,
				Padding = new Thickness(10, 5),
				Child = _status,
			};
		DockPanel.SetDock(statusBar, Dock.Bottom);
		root.Children.Add(statusBar);

		Grid body = new();
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(280)));
		body.ColumnDefinitions.Add(
			new ColumnDefinition(GridLength.Auto));
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

		StackPanel treePane =
			new()
			{
				Margin = new Thickness(10),
				Spacing = 8,
			};
		treePane.Children.Add(
			new TextBlock
			{
				Text = "Song objects",
				FontWeight = FontWeight.SemiBold,
			});
		treePane.Children.Add(_tree);

		Border treeBorder =
			new()
			{
				BorderThickness = new Thickness(0, 0, 1, 0),
				BorderBrush = Brushes.Gray,
				Child = treePane,
			};
		Grid.SetColumn(treeBorder, 0);
		body.Children.Add(treeBorder);

		Border separator =
			new()
			{
				Width = 4,
				Background = Brushes.Transparent,
			};
		Grid.SetColumn(separator, 1);
		body.Children.Add(separator);

		StackPanel editor =
			new()
			{
				Margin = new Thickness(24),
			};
		editor.Children.Add(_editorTitle);
		editor.Children.Add(_editorDetails);
		editor.Children.Add(_editorHint);

		ScrollViewer editorScroller =
			new()
			{
				Content = editor,
			};
		Grid.SetColumn(editorScroller, 2);
		body.Children.Add(editorScroller);

		root.Children.Add(body);
		return root;
	}

	private Menu BuildMenu()
	{
		MenuItem newItem = new() { Header = "_New" };
		newItem.Click += (_, _) => NewDocument();

		MenuItem openItem = new() { Header = "_Open..." };
		openItem.Click += async (_, _) => await OpenDocumentAsync();

		MenuItem saveItem = new() { Header = "_Save" };
		saveItem.Click += async (_, _) => await SaveDocumentAsync();

		MenuItem saveAsItem = new() { Header = "Save _As..." };
		saveAsItem.Click += async (_, _) => await SaveDocumentAsAsync();

		MenuItem file =
			new()
			{
				Header = "_File",
				ItemsSource = new object[]
				{
					newItem,
					openItem,
					new Separator(),
					saveItem,
					saveAsItem,
				},
			};

		return new Menu
		{
			ItemsSource = new object[] { file },
		};
	}

	private void NewDocument()
	{
		if (!CanReplaceDocument())
			return;

		_workspace.New();
		RefreshDocumentView("New song");
	}

	private async Task OpenDocumentAsync()
	{
		if (!CanReplaceDocument())
			return;

		if (!StorageProvider.CanOpen)
		{
			SetStatus("This platform does not provide an open-file picker.");
			return;
		}

		IReadOnlyList<IStorageFile> files =
			await StorageProvider.OpenFilePickerAsync(
				new FilePickerOpenOptions
				{
					Title = "Open Heresy song",
					AllowMultiple = false,
					FileTypeFilter = new[] { SongFileType },
				});

		if (files.Count == 0)
			return;

		string? path = files[0].TryGetLocalPath();
		if (path is null)
		{
			SetStatus("The selected file does not expose a local filesystem path.");
			return;
		}

		try
		{
			_workspace.Open(path);
			RefreshDocumentView($"Opened {_workspace.DisplayName}");
		}
		catch (Exception ex)
		{
			SetStatus($"Open failed: {ex.Message}");
		}
	}

	private async Task SaveDocumentAsync()
	{
		if (_workspace.FilePath is null)
		{
			await SaveDocumentAsAsync();
			return;
		}

		try
		{
			_workspace.Save();
			RefreshDocumentView($"Saved {_workspace.DisplayName}");
		}
		catch (Exception ex)
		{
			SetStatus($"Save failed: {ex.Message}");
		}
	}

	private async Task SaveDocumentAsAsync()
	{
		if (!StorageProvider.CanSave)
		{
			SetStatus("This platform does not provide a save-file picker.");
			return;
		}

		IStorageFile? file =
			await StorageProvider.SaveFilePickerAsync(
				new FilePickerSaveOptions
				{
					Title = "Save Heresy song",
					SuggestedFileName =
						_workspace.FilePath is null
							? "song.json"
							: Path.GetFileName(_workspace.FilePath),
					DefaultExtension = "json",
					FileTypeChoices = new[] { SongFileType },
				});

		if (file is null)
			return;

		string? path = file.TryGetLocalPath();
		if (path is null)
		{
			SetStatus("The selected destination does not expose a local filesystem path.");
			return;
		}

		try
		{
			_workspace.SaveAs(path);
			RefreshDocumentView($"Saved {_workspace.DisplayName}");
		}
		catch (Exception ex)
		{
			SetStatus($"Save failed: {ex.Message}");
		}
	}

	private bool CanReplaceDocument()
	{
		if (!_workspace.IsModified)
			return true;

		SetStatus(
			"The song has unsaved changes. Save it before replacing the active document.");
		return false;
	}

	private void RefreshDocumentView(string status)
	{
		Title =
			_workspace.IsModified
				? $"{_workspace.DisplayName} * — Heresy"
				: $"{_workspace.DisplayName} — Heresy";

		SongTreeItemViewModel root =
			SongTreeItemViewModel.Create(
				_workspace.Document,
				_workspace.Document.Root);

		TreeViewItem rootItem = BuildTreeItem(root);
		_tree.ItemsSource = new object[] { rootItem };
		ShowTreeItem(root);
		SetStatus(status);
	}

	private TreeViewItem BuildTreeItem(SongTreeItemViewModel item)
	{
		TextBlock header =
			new()
			{
				Text = item.IsMissingReference
					? $"⚠ {item.DisplayName}"
					: item.DisplayName,
				FontStyle = item.IsMissingReference
					? FontStyle.Italic
					: FontStyle.Normal,
			};

		TreeViewItem result =
			new()
			{
				Header = header,
				IsExpanded = item.IsFolder,
			};

		result.ItemsSource = BuildChildItems(item.Children);
		result.PointerPressed += (_, _) => ShowTreeItem(item);
		return result;
	}

	private object[] BuildChildItems(
		IReadOnlyList<SongTreeItemViewModel> children)
	{
		object[] result = new object[children.Count];
		for (int index = 0; index < children.Count; index++)
			result[index] = BuildTreeItem(children[index]);
		return result;
	}

	private void ShowTreeItem(SongTreeItemViewModel item)
	{
		_editorTitle.Text = item.DisplayName;

		if (item.IsFolder)
		{
			_editorDetails.Text =
				$"Folder • {item.Children.Count} item{(item.Children.Count == 1 ? string.Empty : "s")}";
			_editorHint.Text =
				"Folders organize the song tree only. Moving an object here never changes its identity or musical references.";
			return;
		}

		string id = item.ObjectId?.Value.ToString() ?? "—";
		_editorDetails.Text = item.IsMissingReference
			? $"Missing {item.Kind} reference • Object ID {id}"
			: $"{item.Kind} • Object ID {id}";

		_editorHint.Text = item.IsMissingReference
			? "The reference is preserved. Restoring the object with this ID will make it valid again."
			: EditorHintFor(item.Kind);
	}

	private static string EditorHintFor(SongObjectKind kind)
		=> kind switch
		{
			SongObjectKind.Sample =>
				"Sample metadata and external-asset diagnostics will appear here next.",
			SongObjectKind.Pattern =>
				"The tracker pattern grid will occupy this editor surface.",
			SongObjectKind.Sequence =>
				"Sequences will share the pattern editor surface and scroll seamlessly across pattern boundaries.",
			SongObjectKind.Instrument =>
				"Instrument divisions, tone specifications and the tone table will be edited here.",
			SongObjectKind.Envelope =>
				"Envelope parameters will be edited here.",
			_ =>
				"No editor is available for this object yet.",
		};

	private void SetStatus(string text)
	{
		_status.Text = text;
	}
}
