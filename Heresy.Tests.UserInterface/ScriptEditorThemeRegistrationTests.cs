using System;
using System.Linq;

using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptEditorThemeRegistrationTests
{
	[Test]
	public void DesktopAppRegistersAvaloniaEditFluentStyles()
	{
		// FluentTheme alone does not provide AvaloniaEdit's TextEditor
		// template: without this resource its editor area renders blank.
		// The production Initialize path calls this same method. A
		// detached styles collection avoids depending on Avalonia's
		// platform-specific asset loader in the NUnit process.
		Styles styles = new();
		Heresy.App.RegisterStyles(styles);

		Assert.That(styles.First(), Is.InstanceOf<FluentTheme>());
		StyleInclude? editorStyles = styles
			.OfType<StyleInclude>()
			.SingleOrDefault(style => style.Source == new Uri(
				"avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"));
		Assert.That(editorStyles, Is.Not.Null,
			"The real desktop startup must include AvaloniaEdit's Fluent styles.");
	}
}
