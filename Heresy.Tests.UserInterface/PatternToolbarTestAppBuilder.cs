using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using System;

[assembly: AvaloniaTestApplication(typeof(Heresy.Tests.UserInterface.PatternToolbarTestAppBuilder))]

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Dedicated real-window headless host for toolbar layout, focus, and
/// keyboard tests. Other model-only NUnit tests remain plain tests.
/// </summary>
public sealed class PatternToolbarTestAppBuilder
{
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<PatternToolbarTestApplication>()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class PatternToolbarTestApplication : Application
{
	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
		// Exercise the real MIT TreeDataGrid template in headless tests.
		Uri theme = new("avares://TreeDataGrid.Avalonia/Themes/Fluent.axaml");
		Styles.Add(new StyleInclude(theme) { Source = theme });
	}
}
