using Heresy;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DesktopFilePickerPolicyTests
{
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("gtk", false)]
    [TestCase("managed", false)]
    [TestCase("MANAGED", false)]
    [TestCase("portal", true)]
    [TestCase("PORTAL", true)]
    [TestCase(" portal ", true)]
    [TestCase("other", false)]
    public void X11ChooserDefaultsToGtkAndOnlyPortalEnablesDbus(
        string? preference, bool usePortal)
    {
        var options = DesktopFilePickerPolicy.X11Options(preference);
        Assert.That(options.UseDBusFilePicker, Is.EqualTo(usePortal));
    }

    [TestCase(null, true, false)]
    [TestCase("", true, false)]
    [TestCase("gtk", true, false)]
    [TestCase("portal", true, false)]
    [TestCase("managed", true, true)]
    [TestCase("MANAGED", true, true)]
    [TestCase(" managed ", true, true)]
    [TestCase("managed", false, false)]
    [TestCase("gtk", false, false)]
    [TestCase("portal", false, false)]
    public void ManagedChooserIsExplicitLinuxOnly(
        string? preference, bool isLinux, bool expected)
        => Assert.That(DesktopFilePickerPolicy.UseManagedDialogs(
            preference, isLinux), Is.EqualTo(expected));

    [Test]
    public void ConfigurationIsNotSharedBetweenCalls()
    {
        var first = DesktopFilePickerPolicy.X11Options(null);
        first.UseDBusFilePicker = true;
        Assert.That(DesktopFilePickerPolicy.X11Options(null).UseDBusFilePicker,
            Is.False);
    }
}
