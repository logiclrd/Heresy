using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// An accepted graph-plan transaction and its final counts. The IDs in
/// Commit.SelectedRootIds are the explicit new Synth roots, not all copied
/// dependencies or source-side numeric IDs.
/// </summary>
public sealed record FmSynthImportSelectionResult(
    FmImportCommitResult Commit, FmImportTreeSummary Summary);

public sealed class FmSynthImportSelectionDialog : Window
{
	private readonly List<(FmSynthDefinition Synth, CheckBox CheckBox)> _choices = [];
	private readonly SongFmSynthImportSource _source;
	private readonly SongDocument? _destination;
	private bool _openingPlan;

	public FmSynthImportSelectionDialog(
		SongFmSynthImportSource source, SongDocument? destination = null)
	{
		ArgumentNullException.ThrowIfNull(source);
		_source = source;
		_destination = destination;

		Title = "Import synths from Heresy song";
		Width = 540;
		Height = 460;
		MinWidth = 380;
		MinHeight = 280;
		WindowStartupLocation =
			WindowStartupLocation.CenterOwner;

		TextBlock sourceLabel =
			new()
			{
				Text =
					$"Choose synths to import from {System.IO.Path.GetFileName(source.Path)}. " +
					"Import selected opens the editable graph-aware dependency plan. " +
					"Review the choices there, then Import to apply them or Cancel to return.",
				TextWrapping =
					Avalonia.Media.TextWrapping.Wrap,
			};

		StackPanel choices =
			new()
			{
				Spacing = 4,
			};
		foreach (FmSynthDefinition synth in source.Synths)
		{
			CheckBox checkBox =
				new()
				{
					Content =
						$"{synth.Name}  (Object {synth.Id.Value})",
					IsChecked = true,
				};
			_choices.Add(
				(synth, checkBox));
			choices.Children.Add(checkBox);
		}

		ScrollViewer scroll =
			new()
			{
				Content = choices,
				VerticalScrollBarVisibility =
					ScrollBarVisibility.Auto,
			};

		Button selectAll =
			new()
			{
				Content = "Select all",
			};
		selectAll.Click += (_, _) =>
		{
			foreach (var choice in _choices)
				choice.CheckBox.IsChecked = true;
		};

		Button selectNone =
			new()
			{
				Content = "Select none",
			};
		selectNone.Click += (_, _) =>
		{
			foreach (var choice in _choices)
				choice.CheckBox.IsChecked = false;
		};

		Button cancel =
			new()
			{
				Content = "Cancel",
				MinWidth = 84,
			};
		cancel.Click += (_, _) =>
			Close(null);

		Button import =
			new()
			{
				Content = "Import selected",
				MinWidth = 112,
				IsEnabled = destination is not null,
			};
		import.Click += async (_, _) =>
		{
			if (_openingPlan)
				return;
			ObjectId[] selected = _choices
				.Where(choice => choice.CheckBox.IsChecked == true)
				.Select(choice => choice.Synth.Id).ToArray();
			if (selected.Length == 0)
			{
				sourceLabel.Text = "Select at least one Synth to import.";
				return;
			}
			if (destination is null)
			{
				sourceLabel.Text = "An import destination is required.";
				return;
			}

			_openingPlan = true;
			import.IsEnabled = false;
			try
			{
				FmSynthImportSelectionResult? accepted =
					await ReviewAndCommitAsync(selected,
						plan => plan.ShowDialog<FmImportCommitResult?>(this));
				if (accepted is not null)
					Close(accepted);
			}
			catch (Exception error)
			{
				// A failure constructing/opening the plan leaves checked roots
				// available; transaction failures are reported inside the plan.
				sourceLabel.Text = $"Could not open import plan: {error.Message}";
			}
			finally
			{
				_openingPlan = false;
				import.IsEnabled = destination is not null;
			}
		};

		DialogActionLayout.ConfigureButtons(import, cancel);

		StackPanel selectionActions =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				Spacing = 6,
			};
		selectionActions.Children.Add(
			selectAll);
		selectionActions.Children.Add(
			selectNone);

		StackPanel dialogActions =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				HorizontalAlignment =
					HorizontalAlignment.Right,
				Spacing = 8,
			};
		dialogActions.Children.Add(cancel);
		dialogActions.Children.Add(import);
		dialogActions.VerticalAlignment = VerticalAlignment.Bottom;

		Grid root =
			new()
			{
				Margin = new Thickness(14),
				RowDefinitions =
					new RowDefinitions(
						"Auto,Auto,*,Auto"),
				RowSpacing = 10,
			};
		Grid.SetRow(
			sourceLabel,
			0);
		Grid.SetRow(
			selectionActions,
			1);
		Grid.SetRow(
			scroll,
			2);
		Grid.SetRow(
			dialogActions,
			3);
		root.Children.Add(sourceLabel);
		root.Children.Add(selectionActions);
		root.Children.Add(scroll);
		root.Children.Add(dialogActions);

		Content = root;
	}

	/// <summary>
	/// A testable, nonmutating-until-acceptance path used by the existing
	/// Import selected button. Cancel returns null and keeps the selection
	/// dialog alive with the same checked roots. Only a result actually
	/// committed by this very plan is allowed to leave the selector.
	/// </summary>
	public async Task<FmSynthImportSelectionResult?> ReviewAndCommitAsync(
		IReadOnlyList<ObjectId> selected,
		Func<FmSynthImportPlanPreviewDialog, Task<FmImportCommitResult?>> present)
	{
		ArgumentNullException.ThrowIfNull(selected);
		ArgumentNullException.ThrowIfNull(present);
		if (_destination is null)
			throw new InvalidOperationException("An import destination is required.");
		if (selected.Count == 0)
			return null;

		FmSynthImportPlanPreviewDialog plan = new(
			_source, selected, _destination, enableCommit: true);
		FmImportCommitResult? accepted = await present(plan);
		if (accepted?.IsCommitted != true ||
			!ReferenceEquals(accepted, plan.CommittedResult))
			return null;
		return new FmSynthImportSelectionResult(accepted,
			plan.Projection.Summary);
	}
}
