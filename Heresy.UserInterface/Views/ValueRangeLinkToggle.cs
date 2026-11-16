using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Heresy.UserInterface.Images;

namespace Heresy.UserInterface.Views;

/// <summary>
/// UI-only relationship between the bounds. The authored model stores two
/// ordinary numbers; no persistent flag or audio-only update path is added.
/// </summary>
public sealed class ValueRangeLinkToggle : ToggleButton
{
	public ValueRangeLinkToggle(bool initiallyLinked)
	{
		Name = "ValueRangeLink";
		Width = 38;
		MinHeight = 32;
		VerticalAlignment = VerticalAlignment.Bottom;
		HorizontalAlignment = HorizontalAlignment.Center;
		IsChecked = initiallyLinked;
		ToolTip.SetTip(this, "Link Vmin and Vmax symmetrically about zero");
		RefreshIcon();
		PropertyChanged += (_, e) =>
		{
			if (e.Property == IsCheckedProperty)
				RefreshIcon();
		};
	}

	private void RefreshIcon()
		=> Content = IsChecked == true ? new ChainLinked() : new ChainUnlinked();
}

/// <summary>Centralize the one precise recentering formula and sign rules
/// for the shared Envelope editor and the Synth Noise/Oscillator inspectors.
/// </summary>
public static class ValueRangeSymmetry
{
	public static bool IsSymmetric(double minimum, double maximum)
		=> minimum == -maximum;

	public static (double Minimum, double Maximum) Center(
		double minimum, double maximum)
	{
		// Order is intentional: this is the documented (Vmax - Vmin) / 2,
		// not a midpoint calculated from the previous asymmetric range.
		double halfWidth = (maximum - minimum) / 2.0;
		if (!double.IsFinite(halfWidth))
			throw new ArgumentException("The symmetric value range must be finite.");
		return (-halfWidth, halfWidth);
	}

	public static (double Minimum, double Maximum) SetMinimum(
		double minimum, double maximum, bool linked)
		=> (minimum, linked ? -minimum : maximum);

	public static (double Minimum, double Maximum) SetMaximum(
		double minimum, double maximum, bool linked)
		=> (linked ? -maximum : minimum, maximum);
}
