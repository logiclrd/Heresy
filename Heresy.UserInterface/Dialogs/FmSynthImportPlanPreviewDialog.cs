using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// Hands-on visual preview of the new import plan. It intentionally has no
/// accepted/import result: the separate atomic commit engine is not written
/// yet, and no decision here can be applied by the legacy import operation.
/// </summary>
public sealed class FmSynthImportPlanPreviewDialog : Window
{
    private readonly SongDocument _destination;
    private readonly FmSynthImportPlan _plan;
    private readonly FmSynthImportTreeProjection _projection;
    private readonly TreeDataGrid _grid;
    private readonly FmSynthImportGridAdapter _adapter;
    private readonly TextBlock _summary;
    private readonly TextBlock _validation;
    private readonly Button _unavailableImport;

    public FmSynthImportPlanPreviewDialog(SongFmSynthImportSource source,
        IReadOnlyList<ObjectId> selectedSynths, SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedSynths);
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        if (selectedSynths.Count == 0)
            throw new ArgumentException("Select at least one Synth.", nameof(selectedSynths));

        FmImportDiscoveredGraph discovery =
            FmSynthImportGraphDiscovery.Discover(source, selectedSynths);
        _plan = FmSynthImportExactMatcher.CreatePlan(
            [new FmImportExactMatchSource(source, discovery)], destination);
        _projection = new FmSynthImportTreeProjection(_plan);

        Title = "Import Synths — graph-aware plan preview";
        Width = 1170;
        Height = 790;
        MinWidth = 690;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        TextBlock introduction = new()
        {
            Text = "Preview only — edit the proposed dependency plan below. " +
                "No changes in this window are imported or applied to the song. " +
                "The new atomic import command will be connected in a later slice.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DarkOrange,
        };

        _grid = new TreeDataGrid
        {
            CanUserSortColumns = false,
            CanUserResizeColumns = true,
            ShowColumnHeaders = true,
            MinHeight = 220,
        };
        _adapter = new FmSynthImportGridAdapter(_projection, _grid,
            CreateDependencyCell, CreateActionCell);

        _summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        _validation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };

        StackPanel summaryContents = new() { Spacing = 5 };
        summaryContents.Children.Add(new TextBlock
        {
            Text = "Proposed import summary",
            FontWeight = FontWeight.Bold,
        });
        summaryContents.Children.Add(_summary);
        summaryContents.Children.Add(_validation);
        Border summaryBorder = new()
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9),
            Child = new ScrollViewer
            {
                Content = summaryContents,
                MaxHeight = 175,
                VerticalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };

        Button reset = new()
        {
            Content = "Reset to defaults",
            MinWidth = 125,
        };
        reset.Click += (_, _) => _projection.Reset();

        Button close = new()
        {
            Content = "Close preview",
            MinWidth = 112,
        };
        close.Click += (_, _) => Close();

        _unavailableImport = new Button
        {
            Content = "Import (not available yet)",
            IsEnabled = false,
            MinWidth = 178,
        };
        ToolTip.SetTip(_unavailableImport,
            "Import requires the Stage-B transaction, final destination ID " +
            "allocation, undo and rollback implementation.");

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        actions.Children.Add(reset);
        actions.Children.Add(close);
        actions.Children.Add(_unavailableImport);

        Grid layout = new()
        {
            Margin = new Thickness(13),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 9,
        };
        Grid.SetRow(introduction, 0);
        Grid.SetRow(_grid, 1);
        Grid.SetRow(summaryBorder, 2);
        Grid.SetRow(actions, 3);
        layout.Children.Add(introduction);
        layout.Children.Add(_grid);
        layout.Children.Add(summaryBorder);
        layout.Children.Add(actions);

        Content = layout;
        RefreshSummary();
        _projection.Changed += OnProjectionChanged;
        Closed += (_, _) =>
        {
            _projection.Changed -= OnProjectionChanged;
            _adapter.Dispose();
        };
    }

    public FmSynthImportTreeProjection Projection => _projection;
    public FmSynthImportPlan Plan => _plan;
    public TreeDataGrid GridControl => _grid;
    public FmSynthImportGridAdapter GridAdapter => _adapter;
    public string SummaryText => _summary.Text ?? string.Empty;

    private void OnProjectionChanged(object? sender, EventArgs args)
        => RefreshSummary();

    private void RefreshSummary()
    {
        FmImportTreeSummary summary = _projection.Summary;
        StringBuilder text = new();
        text.Append($"{summary.NewObjectCount} new objects");
        text.Append($" · {summary.AutomaticallyReusedObjectCount} automatic exact matches");
        text.Append($" · {summary.ManuallyReusedObjectCount} manually reused definitions");
        text.Append($" · {summary.ReusedUseCount} reused references");
        text.Append($" · {summary.ExplicitlyRemappedUseCount} explicit use decisions");
        text.Append($" · {summary.DisconnectedInputCount} disconnected inputs");
        text.Append($" · {summary.ExplicitlyExcludedNodeCount} excluded nodes");
        text.AppendLine();

        if (summary.NewObjects.Count != 0)
            text.AppendLine("Copies: " + string.Join(", ", summary.NewObjects));
        if (summary.ReusedObjects.Count != 0)
            text.AppendLine("Reuse: " + string.Join(", ", summary.ReusedObjects));
        if (summary.ManualMappings.Count != 0)
            text.AppendLine("Manual choices: " + string.Join("; ", summary.ManualMappings));
        if (summary.DisconnectedInputs.Count != 0)
            text.AppendLine("Disconnected: " + string.Join("; ", summary.DisconnectedInputs));
        _summary.Text = text.ToString().TrimEnd();

        _validation.Text = summary.CanImport switch
        {
            true => $"Stage A valid · {summary.WarningCount} warning(s). " +
                "Stage B will run on the future Import action.",
            false => $"Stage A invalid · {summary.ErrorCount} error(s), " +
                $"{summary.WarningCount} warning(s):\n" +
                string.Join("\n", summary.Issues
                    .Where(x => x.Severity == FmImportStageAIssueSeverity.Error)
                    .Select(x => "• " + x.Message)),
            null => "Stage A has not been attached: import validity is unknown.",
        };
        _validation.Foreground = summary.CanImport == false ?
            Brushes.IndianRed : Brushes.Gray;
    }

    private Control CreateDependencyCell(FmSynthImportGridRow row)
    {
        TextBlock text = new()
        {
            Text = row.Label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = row.Snapshot.IsDimmed ? Brushes.Gray : null,
            Margin = new Thickness(3, 2),
        };
        if (!row.Snapshot.IsAutomaticExactMatch)
            return text;
        // Orange conveys provenance, never validation. The label explicitly
        // says "Automatic exact match" in the adjacent action column.
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(75, 255, 160, 25)),
            CornerRadius = new CornerRadius(3),
            Child = text,
        };
    }

    private Control CreateActionCell(FmSynthImportGridRow model)
    {
        FmImportTreeRow row = model.Snapshot;
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(new TextBlock
        {
            Text = row.Action,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = row.IsDimmed ? Brushes.Gray : null,
            MaxWidth = 185,
            TextWrapping = TextWrapping.Wrap,
        });

        FmImportTreeEdit editable = row.AvailableEdits;
        if (editable.HasFlag(FmImportTreeEdit.NavigateToCanonical))
            AddButton(actions, "Go to", () =>
                _adapter.NavigateToCanonical(row.Key));

        if (editable.HasFlag(FmImportTreeEdit.MapDefinition) &&
            row.Object is FmImportObjectKey definition)
            AddMappingPicker(actions, definition, null);

        if (editable.HasFlag(FmImportTreeEdit.OverrideUse) &&
            row.Reference is FmImportReferenceKey use)
            AddMappingPicker(actions, row.Object, use);

        if (editable.HasFlag(FmImportTreeEdit.RenameCopy) &&
            row.Object is FmImportObjectKey named)
        {
            TextBox name = new()
            {
                Text = _plan.GetProposedName(named).Name,
                PlaceholderText = "Copy name",
                MinWidth = 100,
                Width = 140,
            };
            void CommitName()
            {
                string next = name.Text ?? "";
                if (next != _plan.GetProposedName(named).Name)
                    _projection.SetName(named, next);
            }
            name.LostFocus += (_, _) => CommitName();
            name.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    CommitName();
                    args.Handled = true;
                }
            };
            actions.Children.Add(name);
        }

        if (row.Input is FmImportGraphInputKey input)
        {
            if (editable.HasFlag(FmImportTreeEdit.DisconnectInput))
                AddButton(actions, "Disconnect", () =>
                    _projection.SetConnection(input, false));
            if (editable.HasFlag(FmImportTreeEdit.ReconnectInput))
                AddButton(actions, "Reconnect", () =>
                    _projection.SetConnection(input, true));
        }

        if (row.Node is FmImportNodeKey node)
        {
            if (editable.HasFlag(FmImportTreeEdit.ExcludeNode))
                AddButton(actions, "Exclude", () =>
                    _projection.SetNodeExcluded(node, true));
            if (editable.HasFlag(FmImportTreeEdit.IncludeNode))
                AddButton(actions, "Include", () =>
                    _projection.SetNodeExcluded(node, false));
            if (editable.HasFlag(FmImportTreeEdit.PreserveDisconnected))
            {
                bool preserved = _plan.PreservedDisconnectedNodes.Contains(node);
                AddButton(actions, preserved ? "Unpreserve" : "Preserve",
                    () => _projection.PreserveNode(node, !preserved));
            }
        }

        return actions;
    }

    private static void AddButton(StackPanel panel, string caption, Action click)
    {
        Button button = new()
        {
            Content = caption,
            MinWidth = 65,
            Padding = new Thickness(6, 3),
        };
        button.Click += (_, _) => click();
        panel.Children.Add(button);
    }

    private sealed record MappingChoice(
        string Label, FmImportMappingKind Kind, ObjectId? ExistingId,
        bool ClearUseOverride = false)
    {
        public override string ToString() => Label;
    }

    private void AddMappingPicker(StackPanel parent,
        FmImportObjectKey? objectKey, FmImportReferenceKey? use)
    {
        if (objectKey is null && use is null)
            return;
        if (use is null && objectKey is not FmImportObjectKey)
            return;

        FmImportReferenceKind expected = use is FmImportReferenceKey reference
            ? _plan.DiscoveredReferences[reference].Kind
            : KindFor(_plan.DiscoveredObjects[objectKey!.Value].Kind);
        bool canCopy = use is not FmImportReferenceKey copiedUse
            || (_plan.DiscoveredReferences[copiedUse].Target is FmImportObjectKey target
                && _plan.DiscoveredObjects.ContainsKey(target));
        bool allowNone = use is FmImportReferenceKey maybeUse &&
            (expected != FmImportReferenceKind.SoundSource ||
                maybeUse.ToneIndex is null);

        List<MappingChoice> choices = [];
        if (use is FmImportReferenceKey)
            choices.Add(new("Inherit definition", FmImportMappingKind.ImportCopy,
                null, ClearUseOverride: true));
        if (canCopy)
            choices.Add(new("Import separate copy",
                FmImportMappingKind.ImportCopy, null));
        if (allowNone)
            choices.Add(new("None / disconnect", FmImportMappingKind.None, null));
        foreach (SongObject local in _destination.Objects.Values
            .Where(candidate => Compatible(expected, candidate))
            .OrderBy(candidate => candidate.Name)
            .ThenBy(candidate => candidate.Id.Value))
            choices.Add(new($"Reuse {local.Name} ({local.GetType().Name} #{local.Id.Value})",
                FmImportMappingKind.ReuseExisting, local.Id));

        FmImportMapping current = use is FmImportReferenceKey assigned
            ? _plan.GetReferenceMapping(assigned)
            : _plan.GetDefinitionMapping(objectKey!.Value);
        bool inherited = use is FmImportReferenceKey u
            && !_plan.HasReferenceOverride(u);
        MappingChoice? selected = choices.FirstOrDefault(choice =>
            choice.ClearUseOverride == inherited && (!inherited &&
                choice.Kind == current.Kind && choice.ExistingId == current.ExistingId
                || inherited));
        if (selected is null && current.Kind == FmImportMappingKind.ReuseExisting)
        {
            // Still show the stale, missing choice so a user can repair it.
            selected = new($"Unavailable local #{current.ExistingId?.Value}",
                current.Kind, current.ExistingId);
            choices.Insert(0, selected);
        }

        ComboBox picker = new()
        {
            ItemsSource = choices,
            SelectedItem = selected,
            MinWidth = 145,
            MaxWidth = 265,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(picker, use is null ?
            "Change this shared definition for all uses" :
            "Override this particular reference; other uses are unchanged");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not MappingChoice chosen ||
                ReferenceEquals(chosen, selected))
                return;
            if (use is FmImportReferenceKey selectedUse)
            {
                if (chosen.ClearUseOverride)
                    _projection.ClearUse(selectedUse);
                else
                    _projection.ApplyUse(selectedUse, chosen.Kind, chosen.ExistingId);
            }
            else if (objectKey is FmImportObjectKey selectedObject)
                _projection.ApplyDefinition(selectedObject,
                    chosen.Kind, chosen.ExistingId);
        };
        parent.Children.Add(picker);
    }

    private static FmImportReferenceKind KindFor(SongObjectKind kind)
        => kind switch
        {
            SongObjectKind.Envelope => FmImportReferenceKind.Envelope,
            SongObjectKind.Filter => FmImportReferenceKind.Filter,
            _ => FmImportReferenceKind.SoundSource,
        };

    private static bool Compatible(FmImportReferenceKind expected, SongObject local)
        => expected switch
        {
            FmImportReferenceKind.SoundSource => local is
                FmSynthDefinition or SampleDefinition or InstrumentDefinition
                or ScriptInstrumentDefinition,
            FmImportReferenceKind.Envelope => local is
                NodeEnvelopeDefinition or ScriptEnvelopeDefinition,
            FmImportReferenceKind.Filter => local is FilterDefinition,
            _ => false,
        };
}
