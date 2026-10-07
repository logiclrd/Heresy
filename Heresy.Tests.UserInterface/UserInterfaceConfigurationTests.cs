using AwesomeAssertions;

using Avalonia.Media;

using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class UserInterfaceConfigurationTests
{
	[Test]
	public void DefaultPatternRowHighlightColoursUseThemeBlendingAlpha()
	{
		UserInterfaceConfiguration configuration = new();

		configuration.MajorPatternRowHighlight
			.Should().Be(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
		configuration.MinorPatternRowHighlight
			.Should().Be(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
		configuration.PatternSelectionHighlight
			.Should().Be(Color.FromArgb(0x60, 0x40, 0x80, 0xFF));
		configuration.PatternPlaybackRowHighlight
			.Should().Be(Color.FromArgb(0x80, 0x00, 0x80, 0x00));
	}
}
