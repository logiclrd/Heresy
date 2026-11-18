using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

/// <summary>An Avalonia-only row wrapper. The projection owns identity/decisions;
/// the grid owns transient column presentation and IndexPath navigation.</summary>
public sealed class FmSynthImportGridRow
{
    public FmSynthImportGridRow(FmImportTreeRow snapshot,
        IReadOnlyList<FmSynthImportGridRow> children, bool expanded)
    {
        Snapshot = snapshot;
        Children = children;
        IsExpanded = expanded;
    }

    public FmImportTreeRow Snapshot { get; }
    public FmImportTreeRowKey Key => Snapshot.Key;
    public string Label => Snapshot.Label;
    public string Action => Snapshot.Action;
    public string Notes => Snapshot.Reason ?? (Snapshot.IsAlias ? "Linked use" : "");
    public IReadOnlyList<FmSynthImportGridRow> Children { get; }
    public bool HasChildren => Children.Count != 0;

    // Writable: HierarchicalExpanderColumn uses a two-way expression binding.
    // Expansion is also preserved in the adapter's stable-key set.
    public bool IsExpanded { get; set; }
}

/// <summary>
/// Binds the immutable import projection to Wieslaw Soltes's MIT TreeDataGrid.
/// IndexPath values are always temporary; only FmImportTreeRowKey survives
/// recalculation or canonical ownership transfer.
/// </summary>
public sealed class FmSynthImportGridAdapter : IDisposable
{
    private readonly FmSynthImportTreeProjection _projection;
    private readonly TreeDataGrid _grid;
    private readonly Func<FmSynthImportGridRow, Control> _createDependency;
    private readonly Func<FmSynthImportGridRow, Control> _createAction;
    private readonly Func<FmSynthImportGridRow, Control> _createNotes;
    private readonly Dictionary<FmImportTreeRowKey, IndexPath> _paths = [];
    private readonly HashSet<FmImportTreeRowKey> _expanded = [];
    private FmImportTreeRowKey? _selected;
    private HierarchicalTreeDataGridSource<FmSynthImportGridRow>? _source;
    private bool _disposed;
    private bool _rebinding;

    public FmSynthImportGridAdapter(
        FmSynthImportTreeProjection projection,
        TreeDataGrid grid,
        Func<FmSynthImportGridRow, Control> createDependency,
        Func<FmSynthImportGridRow, Control> createAction,
        Func<FmSynthImportGridRow, Control> createNotes)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _createDependency = createDependency ?? throw new ArgumentNullException(nameof(createDependency));
        _createAction = createAction ?? throw new ArgumentNullException(nameof(createAction));
        _createNotes = createNotes ?? throw new ArgumentNullException(nameof(createNotes));
        _projection.Changed += OnProjectionChanged;
        Rebind();
    }

    public HierarchicalTreeDataGridSource<FmSynthImportGridRow> Source
        => _source ?? throw new ObjectDisposedException(nameof(FmSynthImportGridAdapter));

    public IReadOnlyDictionary<FmImportTreeRowKey, IndexPath> Paths => _paths;
    public FmImportTreeRowKey? Selected => _selected;

    private void OnProjectionChanged(object? sender, EventArgs args) => Rebind();

    private void OnRowExpanded(object? sender, TreeDataGridRowModelEventArgs args)
    {
        if (!_rebinding && args.Row.Model is FmSynthImportGridRow row)
            _expanded.Add(row.Key);
    }

    private void OnRowCollapsed(object? sender, TreeDataGridRowModelEventArgs args)
    {
        if (!_rebinding && args.Row.Model is FmSynthImportGridRow row)
            _expanded.Remove(row.Key);
    }

    private void OnSelectionChanged(object? sender,
        TreeDataGridSelectionChangedEventArgs<FmSynthImportGridRow> args)
    {
        if (!_rebinding)
            _selected = _source?.RowSelection?.SelectedItem?.Key;
    }

    public void Rebind()
    {
        if (_disposed) return;
        _rebinding = true;
        try
        {
            HierarchicalTreeDataGridSource<FmSynthImportGridRow>? previous = _source;
            if (previous is not null)
            {
                previous.RowExpanded -= OnRowExpanded;
                previous.RowCollapsed -= OnRowCollapsed;
                if (previous.RowSelection is not null)
                    previous.RowSelection.SelectionChanged -= OnSelectionChanged;
            }

            _paths.Clear();
            FmSynthImportGridRow Create(FmImportTreeRow row, IndexPath path)
            {
                _paths.Add(row.Key, path);
                FmSynthImportGridRow[] children = row.Children
                    .Select((child, index) => Create(child, path.Append(index)))
                    .ToArray();
                return new FmSynthImportGridRow(row, Array.AsReadOnly(children),
                    _expanded.Contains(row.Key) || row.IsExpanded);
            }

            FmSynthImportGridRow[] roots = _projection.Roots
                .Select((root, index) => Create(root, new IndexPath(index)))
                .ToArray();
            HierarchicalTreeDataGridSource<FmSynthImportGridRow> next =
                new(roots);
            next.Columns.Add(new HierarchicalExpanderColumn<FmSynthImportGridRow>(
                new TemplateColumn<FmSynthImportGridRow>(
                    "Dependency",
                    new FuncDataTemplate<FmSynthImportGridRow>(
                        (row, _) => _createDependency(row!)),
                    width: new GridLength(3, GridUnitType.Star)),
                row => row.Children,
                row => row.HasChildren,
                row => row.IsExpanded));
            next.Columns.Add(new TemplateColumn<FmSynthImportGridRow>(
                "Plan",
                new FuncDataTemplate<FmSynthImportGridRow>(
                    (row, _) => _createAction(row!)),
                width: new GridLength(4, GridUnitType.Star)));
            // TextColumn's Generic template likewise leaves its TextBlock
            // foreground unspecified. Use the same explicit theme-aware text
            // binding as the Dependency and Plan template cells.
            next.Columns.Add(new TemplateColumn<FmSynthImportGridRow>(
                "Notes",
                new FuncDataTemplate<FmSynthImportGridRow>(
                    (row, _) => _createNotes(row!)),
                width: new GridLength(2, GridUnitType.Star)));

            _source = next;
            next.RowExpanded += OnRowExpanded;
            next.RowCollapsed += OnRowCollapsed;
            next.RowSelection!.SelectionChanged += OnSelectionChanged;
            _grid.Source = next;

            // Set expansion before restoring selection. IndexPaths are rebuilt
            // from keys every time, never retained as stable identities.
            foreach ((FmImportTreeRowKey key, IndexPath path) in _paths
                .OrderBy(pair => pair.Value.Count))
                if (_expanded.Contains(key) || _projection.Rows[key].IsExpanded)
                    next.Expand(path);

            if (_selected is FmImportTreeRowKey keySelected
                && _paths.TryGetValue(keySelected, out IndexPath selectedPath))
                next.RowSelection!.SelectedIndex = selectedPath;
            else
                _selected = null;

            previous?.Dispose();
        }
        finally
        {
            _rebinding = false;
        }
    }

    /// <summary>Expand and scroll to the current canonical row. Does not use
    /// the alias's old ordinal after a graph/Filter mapping changes.</summary>
    public bool NavigateToCanonical(FmImportTreeRowKey alias)
    {
        FmImportTreeRowKey? target = _projection.NavigateToCanonical(alias);
        if (target is not FmImportTreeRowKey key ||
            !_paths.TryGetValue(key, out IndexPath path))
            return false;
        for (int depth = 1; depth < path.Count; depth++)
        {
            IndexPath parent = path.Slice(0, depth);
            if (_source!.TryGetModelAt(parent, out FmSynthImportGridRow? model))
                _expanded.Add(model.Key);
            _source.Expand(parent);
        }
        _selected = key;
        _source!.RowSelection!.SelectedIndex = path;
        int rowIndex = _source.Rows.ModelIndexToRowIndex(path);
        if (rowIndex >= 0)
            _grid.RowsPresenter?.BringIntoView(rowIndex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projection.Changed -= OnProjectionChanged;
        if (_source is not null)
        {
            _source.RowExpanded -= OnRowExpanded;
            _source.RowCollapsed -= OnRowCollapsed;
            if (_source.RowSelection is not null)
                _source.RowSelection.SelectionChanged -= OnSelectionChanged;
            _grid.Source = null;
            _source.Dispose();
            _source = null;
        }
    }
}
