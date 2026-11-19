using Heresy;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DesktopFilePickerPolicyTests
{
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("gtk", false)]
    [TestCase("portal", true)]
    [TestCase("PORTAL", true)]
    [TestCase(" portal ", true)]
    [TestCase("other", false)]
    public void X11PickerDefaultsToOwnerTransientGtkWithOptionalPortalComparison(
        string? preference, bool usePortal)
    {
        var options = DesktopFilePickerPolicy.X11Options(preference);
        Assert.That(options.UseDBusFilePicker, Is.EqualTo(usePortal));
    }

    [Test]
    public void ConfigurationIsNotSharedBetweenCalls()
    {
        var first = DesktopFilePickerPolicy.X11Options(null);
        first.UseDBusFilePicker = true;
        Assert.That(DesktopFilePickerPolicy.X11Options(null).UseDBusFilePicker,
            Is.False);
    }
}
