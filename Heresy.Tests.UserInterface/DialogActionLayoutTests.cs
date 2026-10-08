using AwesomeAssertions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.UserInterface.Dialogs;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DialogActionLayoutTests
{
	[Test]
	public void ConfirmAndCancelButtonsUseStandardDialogKeyboardRoles()
	{
		Button accept = new();
		Button cancel = new();

		DialogActionLayout.ConfigureButtons(accept, cancel);

		accept.IsDefault.Should().BeTrue();
		accept.IsCancel.Should().BeFalse();
		cancel.IsCancel.Should().BeTrue();
		cancel.IsDefault.Should().BeFalse();
	}

	[Test]
	public void FooterOccupiesBottomRightBelowFlexibleContent()
	{
		Border content = new();
		StackPanel actions = new();
		Thickness margin = new(18);

		Grid layout =
			DialogActionLayout.Create(
				content,
				actions,
				margin,
				spacing: 12);

		layout.Children.Should().ContainInOrder(content, actions);
		layout.RowDefinitions.Should().HaveCount(2);
		layout.RowDefinitions[0].Height.IsStar.Should().BeTrue();
		layout.RowDefinitions[1].Height.IsAuto.Should().BeTrue();
		Grid.GetRow(content).Should().Be(0);
		Grid.GetRow(actions).Should().Be(1);
		actions.HorizontalAlignment.Should().Be(HorizontalAlignment.Right);
		actions.VerticalAlignment.Should().Be(VerticalAlignment.Bottom);
		layout.Margin.Should().Be(margin);
		layout.RowSpacing.Should().Be(12);
	}
}
