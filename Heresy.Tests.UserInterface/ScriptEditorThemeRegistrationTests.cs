using System;
using System.Linq;

using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

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
		Heresy.App app = new();
		app.Initialize();

		Assert.That(app.Styles.First(), Is.InstanceOf<FluentTheme>());
		StyleInclude? editorStyles = app.Styles
			.OfType<StyleInclude>()
			.SingleOrDefault(style => style.Source == new Uri(
				"avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"));
		Assert.That(editorStyles, Is.Not.Null,
			"The real desktop startup must include AvaloniaEdit's Fluent styles.");
	}
}
