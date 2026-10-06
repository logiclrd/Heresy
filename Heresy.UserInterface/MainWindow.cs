using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Heresy.Core.Objects;
using Heresy.UserInterface.Dialogs;
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

	private static readonly DataFormat<SongTreeNode> TreeNodeFormat =
		DataFormat.CreateInProcessFormat<SongTreeNode>("Heresy.SongTreeNode");

	private readonly DocumentWorkspace _workspace;
	private readonly Dictionary<SongTreeSection, TreeView> _trees = [];
	private readonly TextBlock _status;

	private SongTreeItemViewModel? _selectedItem;
	private TreeView? _selectedTree;
	private SongTreeItemViewModel? _dragCandidate;
	private PointerPressedEventArgs? _dragTrigger;
	private Point _dragStart;
	private bool _dragInProgress;

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
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		body.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));
		body.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));

		AddSectionPane(body, SongTreeSection.Sequences, row: 0, column: 0);
		AddSectionPane(body, SongTreeSection.Patterns, row: 0, column: 1);
		AddSectionPane(body, SongTreeSection.Samples, row: 1, column: 0);
		AddSectionPane(body, SongTreeSection.Instruments, row: 1, column: 1);

		root.Children.Add(body);
		return root;
	}

	private void AddSectionPane(
		Grid body,
		SongTreeSection section,
		int row,
		int column)
	{
		TreeView tree = new();
		_trees.Add(section, tree);
		tree.SelectionChanged += (_, _) =>
		{
			if (tree.SelectedItem is TreeViewItem treeItem
				&& treeItem.Tag is SongTreeItemViewModel item)
			{
				SelectTreeItem(item, tree);
			}
		};

		TextBlock title =
			new()
			{
				Text = SongTreeSections.GetName(section),
				FontSize = 17,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		Button newFolder = new() { Content = "+ Folder" };
		newFolder.Click += async (_, _) => await CreateFolderAsync(section);

		DockPanel header =
			new()
			{
				Margin = new Thickness(8, 6),
			};
		DockPanel.SetDock(newFolder, Dock.Right);
		header.Children.Add(newFolder);
		header.Children.Add(title);

		Grid pane = new();
		pane.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		pane.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));
		Grid.SetRow(header, 0);
		pane.Children.Add(header);
		Grid.SetRow(tree, 1);
		pane.Children.Add(tree);

		Border border =
			new()
			{
				BorderThickness = new Thickness(1),
				BorderBrush = Brushes.Gray,
				Margin = new Thickness(4),
				Child = pane,
			};
		Grid.SetRow(border, row);
		Grid.SetColumn(border, column);
		body.Children.Add(border);
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
			RefreshDocumentView($"Saved {_workspace.DisplayName}", _selectedItem?.Node);
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
			RefreshDocumentView($"Saved {_workspace.DisplayName}", _selectedItem?.Node);
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

	private async Task CreateFolderAsync(SongTreeSection section)
	{
		SongTreeFolder parent = GetFolderForNewChild(section);
		TextPromptDialog dialog =
			new("New folder", "Folder name:", "New Folder");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(_workspace.Document, parent, name);
		RefreshDocumentView($"Created folder {folder.Name}", folder);
	}

	private async Task RenameSelectedAsync()
	{
		SongTreeItemViewModel? selected = _selectedItem;
		if (selected is null)
			return;

		if (selected.Node is SongTreeFolder folder)
		{
			TextPromptDialog dialog =
				new("Rename folder", "Folder name:", folder.Name);
			string? name = await dialog.ShowDialog<string?>(this);
			if (name is null)
				return;

			SongTreeEditor.RenameFolder(_workspace.Document, folder, name);
			RefreshDocumentView($"Renamed folder to {folder.Name}", folder);
			return;
		}

		if (selected.ObjectId is not ObjectId objectId
			|| selected.IsMissingReference)
		{
			SetStatus("Missing object references cannot be renamed.");
			return;
		}

		TextPromptDialog objectDialog =
			new("Rename object", "Object name:", selected.DisplayName);
		string? objectName = await objectDialog.ShowDialog<string?>(this);
		if (objectName is null)
			return;

		SongTreeEditor.RenameObject(_workspace.Document, objectId, objectName);
		RefreshDocumentView($"Renamed object to {objectName.Trim()}", selected.Node);
	}

	private async Task DeleteSelectedAsync()
	{
		SongTreeItemViewModel? selected = _selectedItem;
		if (selected?.Node is not SongTreeObject objectNode)
			return;

		SongTreeFolder parent =
			SongTreeEditor.GetParent(_workspace.Document, objectNode)
			?? _workspace.Document.GetSectionRoot(
				SongTreeSections.ForKind(selected.Kind));

		if (selected.IsMissingReference)
		{
			ConfirmDialog missingDialog =
				new(
					"Remove missing tree entry",
					$"Remove the missing reference '{selected.DisplayName}' from this folder? "
					+ "Other unresolved references to the same object ID are not changed.",
					"Remove");
			if (!await missingDialog.ShowDialog<bool>(this))
				return;

			SongTreeEditor.RemoveNode(_workspace.Document, objectNode);
			RefreshDocumentView("Removed missing tree entry", parent);
			return;
		}

		ObjectId objectId = objectNode.ObjectId;
		SongReferenceAnalysis analysis =
			SongReferenceAnalyzer.Analyze(_workspace.Document);
		SongReference[] meaningfulReferences =
			analysis.References
				.Where(reference =>
					reference.TargetId == objectId
					&& reference.Kind != SongReferenceKind.Tree)
				.ToArray();

		string message = BuildDeleteMessage(
			selected.DisplayName,
			meaningfulReferences,
			analysis.HasOpaqueScriptReferences);
		ConfirmDialog deleteDialog =
			new("Delete object", message, "Delete");
		if (!await deleteDialog.ShowDialog<bool>(this))
			return;

		SongTreeEditor.DeleteObject(_workspace.Document, objectId);
		RefreshDocumentView($"Deleted {selected.DisplayName}", parent);
	}

	private SongTreeFolder GetFolderForNewChild(SongTreeSection section)
	{
		SongTreeFolder sectionRoot = _workspace.Document.GetSectionRoot(section);
		if (_selectedItem is null
			|| SongTreeEditor.GetSection(_workspace.Document, _selectedItem.Node) != section)
		{
			return sectionRoot;
		}

		if (_selectedItem.Node is SongTreeFolder selectedFolder)
			return selectedFolder;

		return SongTreeEditor.GetParent(_workspace.Document, _selectedItem.Node)
			?? sectionRoot;
	}

	private string BuildDeleteMessage(
		string displayName,
		IReadOnlyList<SongReference> references,
		bool hasOpaqueScriptReferences)
	{
		StringBuilder message = new();
		message.Append("Delete '");
		message.Append(displayName);
		message.Append("' from the song?");

		if (references.Count != 0)
		{
			message.Append("\n\nThis object is still referenced by:\n");
			int shown = Math.Min(references.Count, 8);
			for (int index = 0; index < shown; index++)
			{
				message.Append(" • ");
				message.Append(DescribeReference(references[index]));
				message.Append('\n');
			}
			if (shown < references.Count)
			{
				message.Append(" • ");
				message.Append(references.Count - shown);
				message.Append(" more reference(s)\n");
			}
		}

		if (hasOpaqueScriptReferences)
		{
			message.Append(
				"\nOne or more script objects exist. Script references cannot yet be analyzed exactly, so they may also refer to this ID.\n");
		}

		if (references.Count != 0 || hasOpaqueScriptReferences)
		{
			message.Append(
				"\nDeletion will not rewrite those references. They will remain as unresolved object IDs until the object is restored or the references are edited.");
		}
		else
		{
			message.Append("\n\nNo musical references to this object were found.");
		}

		return message.ToString();
	}

	private string DescribeReference(SongReference reference)
	{
		string sourceName = "Song";
		if (reference.SourceObjectId is ObjectId sourceId)
		{
			if (_workspace.Document.TryGet(sourceId, out SongObject? source)
				&& source is not null)
			{
				sourceName = source.Name;
			}
			else
			{
				sourceName = $"Object <{sourceId.Value}>";
			}
		}

		return reference.Kind switch
		{
			SongReferenceKind.RootSequence => "the song root sequence",
			SongReferenceKind.PatternNoteSource => $"{sourceName}: pattern note source",
			SongReferenceKind.SequencePattern => $"{sourceName}: sequence pattern entry",
			SongReferenceKind.InstrumentSource => $"{sourceName}: instrument tone source",
			SongReferenceKind.InstrumentVolumeEnvelope => $"{sourceName}: volume envelope",
			SongReferenceKind.InstrumentPitchEnvelope => $"{sourceName}: pitch envelope",
			SongReferenceKind.InstrumentPanningEnvelope => $"{sourceName}: panning envelope",
			SongReferenceKind.InstrumentFilterEnvelope => $"{sourceName}: filter envelope",
			SongReferenceKind.Tree => $"{sourceName}: tree placement",
			_ => sourceName,
		};
	}

	private void RefreshDocumentView(
		string status,
		SongTreeNode? selectNode = null)
	{
		Title =
			_workspace.IsModified
				? $"{_workspace.DisplayName} * — Heresy"
				: $"{_workspace.DisplayName} — Heresy";

		_selectedItem = null;
		_selectedTree = null;
		TreeView? selectedTree = null;
		TreeViewItem? selectedControl = null;
		SongTreeItemViewModel? selectedItem = null;

		foreach (SongTreeSection section in SongTreeSections.DocumentOrder)
		{
			TreeView tree = _trees[section];
			tree.SelectedItem = null;

			SongTreeItemViewModel sectionRoot =
				SongTreeItemViewModel.Create(
					_workspace.Document,
					_workspace.Document.GetSectionRoot(section));

			TreeViewItem? localSelectedControl = null;
			SongTreeItemViewModel? localSelectedItem = null;
			object[] children = new object[sectionRoot.Children.Count];
			for (int index = 0; index < sectionRoot.Children.Count; index++)
			{
				children[index] = BuildTreeItem(
					tree,
					section,
					sectionRoot.Children[index],
					selectNode,
					ref localSelectedControl,
					ref localSelectedItem);
			}
			tree.ItemsSource = children;

			if (localSelectedControl is not null && localSelectedItem is not null)
			{
				selectedTree = tree;
				selectedControl = localSelectedControl;
				selectedItem = localSelectedItem;
			}
		}

		if (selectedTree is not null
			&& selectedControl is not null
			&& selectedItem is not null)
		{
			selectedTree.SelectedItem = selectedControl;
			SelectTreeItem(selectedItem, selectedTree);
		}

		SetStatus(status);
	}

	private TreeViewItem BuildTreeItem(
		TreeView tree,
		SongTreeSection section,
		SongTreeItemViewModel item,
		SongTreeNode? selectNode,
		ref TreeViewItem? selectedControl,
		ref SongTreeItemViewModel? selectedItem)
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
				Tag = item,
			};

		if (selectNode is not null && ReferenceEquals(item.Node, selectNode))
		{
			selectedControl = result;
			selectedItem = item;
		}

		object[] children = new object[item.Children.Count];
		for (int index = 0; index < item.Children.Count; index++)
		{
			children[index] = BuildTreeItem(
				tree,
				section,
				item.Children[index],
				selectNode,
				ref selectedControl,
				ref selectedItem);
		}
		result.ItemsSource = children;

		result.PointerPressed += (_, e) =>
			OnTreePointerPressed(tree, result, item, e);
		result.PointerMoved += async (_, e) =>
			await OnTreePointerMovedAsync(tree, item, e);
		result.PointerReleased += (_, _) => ClearDragCandidate(item);

		DragDrop.SetAllowDrop(result, true);
		DragDrop.AddDragOverHandler(
			result,
			(_, e) => OnTreeDragOver(item, e));
		DragDrop.AddDropHandler(
			result,
			(_, e) => OnTreeDrop(item, e));

		result.ContextMenu = BuildTreeContextMenu(section, tree, item, result);
		return result;
	}

	private ContextMenu BuildTreeContextMenu(
		SongTreeSection section,
		TreeView tree,
		SongTreeItemViewModel item,
		TreeViewItem control)
	{
		MenuItem newFolder = new() { Header = "New Folder..." };
		newFolder.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await CreateFolderAsync(section);
		};

		MenuItem rename =
			new()
			{
				Header = "Rename...",
				IsEnabled = item.IsFolder || !item.IsMissingReference,
			};
		rename.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await RenameSelectedAsync();
		};

		MenuItem delete =
			new()
			{
				Header = item.IsMissingReference ? "Remove from Tree..." : "Delete Object...",
				IsEnabled = item.Node is SongTreeObject,
			};
		delete.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await DeleteSelectedAsync();
		};

		return new ContextMenu
		{
			ItemsSource = new object[]
			{
				newFolder,
				rename,
				new Separator(),
				delete,
			},
		};
	}

	private void OnTreePointerPressed(
		TreeView tree,
		TreeViewItem control,
		SongTreeItemViewModel item,
		PointerPressedEventArgs e)
	{
		tree.SelectedItem = control;
		SelectTreeItem(item, tree);

		if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
		{
			ClearDragCandidate(item);
			return;
		}

		_dragCandidate = item;
		_dragTrigger = e;
		_dragStart = e.GetPosition(tree);
	}

	private async Task OnTreePointerMovedAsync(
		TreeView tree,
		SongTreeItemViewModel item,
		PointerEventArgs e)
	{
		if (_dragInProgress
			|| !ReferenceEquals(_dragCandidate, item)
			|| _dragTrigger is null)
		{
			return;
		}

		if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
		{
			ClearDragCandidate(item);
			return;
		}

		Point current = e.GetPosition(tree);
		double dx = current.X - _dragStart.X;
		double dy = current.Y - _dragStart.Y;
		if ((dx * dx) + (dy * dy) < 36.0)
			return;

		PointerPressedEventArgs trigger = _dragTrigger;
		_dragCandidate = null;
		_dragTrigger = null;
		_dragInProgress = true;

		DataTransfer data = new();
		data.Add(DataTransferItem.Create(TreeNodeFormat, item.Node));
		try
		{
			await DragDrop.DoDragDropAsync(
				trigger,
				data,
				DragDropEffects.Move);
		}
		finally
		{
			_dragInProgress = false;
		}
	}

	private void ClearDragCandidate(SongTreeItemViewModel item)
	{
		if (ReferenceEquals(_dragCandidate, item))
		{
			_dragCandidate = null;
			_dragTrigger = null;
		}
	}

	private void OnTreeDragOver(
		SongTreeItemViewModel target,
		DragEventArgs e)
	{
		SongTreeNode? node = e.DataTransfer.TryGetValue(TreeNodeFormat);
		if (node is null)
		{
			e.DragEffects = DragDropEffects.None;
			return;
		}

		bool canMove = target.Node switch
		{
			SongTreeFolder folder =>
				SongTreeEditor.CanMoveInto(_workspace.Document, node, folder),
			_ =>
				SongTreeEditor.CanMoveBefore(_workspace.Document, node, target.Node),
		};
		e.DragEffects = canMove
			? DragDropEffects.Move
			: DragDropEffects.None;
	}

	private void OnTreeDrop(
		SongTreeItemViewModel target,
		DragEventArgs e)
	{
		SongTreeNode? node = e.DataTransfer.TryGetValue(TreeNodeFormat);
		if (node is null)
		{
			e.DragEffects = DragDropEffects.None;
			return;
		}

		try
		{
			if (target.Node is SongTreeFolder folder)
				SongTreeEditor.MoveInto(_workspace.Document, node, folder);
			else
				SongTreeEditor.MoveBefore(_workspace.Document, node, target.Node);

			e.DragEffects = DragDropEffects.Move;
			RefreshDocumentView("Reorganized song tree", node);
		}
		catch (InvalidOperationException ex)
		{
			e.DragEffects = DragDropEffects.None;
			SetStatus(ex.Message);
		}
	}

	private void SelectTreeItem(
		SongTreeItemViewModel item,
		TreeView tree)
	{
		if (!ReferenceEquals(_selectedTree, tree))
		{
			foreach (TreeView other in _trees.Values)
			{
				if (!ReferenceEquals(other, tree))
					other.SelectedItem = null;
			}
		}

		_selectedItem = item;
		_selectedTree = tree;
		SetStatus(DescribeSelection(item));
	}

	private static string DescribeSelection(SongTreeItemViewModel item)
	{
		if (item.IsFolder)
		{
			return $"{item.DisplayName} — folder, {item.Children.Count} item{(item.Children.Count == 1 ? string.Empty : "s")}";
		}

		string id = item.ObjectId?.Value.ToString() ?? "—";
		return item.IsMissingReference
			? $"{item.DisplayName} — missing {item.Kind} reference, Object ID {id}"
			: $"{item.DisplayName} — {item.Kind}, Object ID {id}";
	}

	private void SetStatus(string text)
	{
		_status.Text = text;
	}
}
