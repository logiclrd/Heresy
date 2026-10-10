using System;
using System.IO;

using Avalonia.Platform;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ApplicationBrandingTests
{
	[Test]
	public void MainWindowIconIsPackagedAsAnAvaloniaResource()
	{
		// The window opens this resource without relying on a working
		// directory, installed executable path or a second copy of the icon.
		Uri uri = new("avares://Heresy.UserInterface/Images/Icon.ico");
		using Stream stream = AssetLoader.Open(uri);
		using BinaryReader icon = new(stream);
		Assert.Multiple(() =>
		{
			Assert.That(icon.ReadUInt16(), Is.Zero,
				"ICO reserved header must be zero.");
			Assert.That(icon.ReadUInt16(), Is.EqualTo(1),
				"The supplied branding asset must be an icon, not a cursor.");
			Assert.That(icon.ReadUInt16(), Is.GreaterThan(1),
				"The supplied ICO should contain multiple image sizes.");
		});
	}
}
