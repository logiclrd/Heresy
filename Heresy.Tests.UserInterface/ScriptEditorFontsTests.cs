using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AvaloniaEdit;

using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptEditorFontsTests
{
    [Test]
    public void PreferredFontsUseTheExactPriorityOrderRegardlessOfEnumerationOrder()
    {
        string? selected = ScriptEditorFonts.SelectFamilyName(
            ["Monaco", "Courier New", "Noto Mono", "hack", "Commit Mono"],
            _ => true, _ => true);
        Assert.That(selected, Is.EqualTo("Commit Mono"));

        selected = ScriptEditorFonts.SelectFamilyName(
            ["Monaco", "Courier New", "Noto Mono", "HACK"],
            _ => true, _ => true);
        Assert.That(selected, Is.EqualTo("HACK"));
    }

    [Test]
    public void UnusablePreferredFontIsSkippedRatherThanFabricated()
    {
        string? selected = ScriptEditorFonts.SelectFamilyName(
            ["Commit Mono", "Hack", "Noto Mono"],
            name => !string.Equals(name, "Commit Mono",
                StringComparison.OrdinalIgnoreCase),
            _ => true);
        Assert.That(selected, Is.EqualTo("Hack"));
    }

    [Test]
    public void SansSerifMonospacePrecedesOtherMonospaceAndCourier()
    {
        string? selected = ScriptEditorFonts.SelectFamilyName(
            ["Courier New", "Other Fixed", "Generic Sans Fixed", "Proportional Sans"],
            name => name.Contains("Fixed", StringComparison.Ordinal)
                || name == "Courier New",
            name => name.Contains("Sans", StringComparison.Ordinal));

        Assert.That(selected, Is.EqualTo("Generic Sans Fixed"));
    }

    [Test]
    public void OtherMonospacePrecedesCourierButCourierNewPrecedesCourier()
    {
        string? selected = ScriptEditorFonts.SelectFamilyName(
            ["Courier", "Courier New", "Other Fixed"],
            name => name is "Courier" or "Courier New" or "Other Fixed",
            _ => false);
        Assert.That(selected, Is.EqualTo("Other Fixed"));

        selected = ScriptEditorFonts.SelectFamilyName(
            ["Courier", "Courier New"], _ => true, _ => false);
        Assert.That(selected, Is.EqualTo("Courier New"));

        selected = ScriptEditorFonts.SelectFamilyName(
            ["Courier"], _ => true, _ => false);
        Assert.That(selected, Is.EqualTo("Courier"));
    }

    [Test]
    public void NoUsableFontLeavesTheDefaultThemeFontAlone()
    {
        string? selected = ScriptEditorFonts.SelectFamilyName(
            ["Arial", "Courier New", "Missing Font"], _ => false, _ => true);
        Assert.That(selected, Is.Null);
        Assert.That(ScriptEditorFonts.SelectFamilyName(
            [], _ => true, _ => true), Is.Null);
    }

    [AvaloniaTest]
    public void ResolvedFontCanBeAppliedToBothKindsOfScriptTextEditor()
    {
        TextBox expression = new();
        TextEditor source = new();
        var originalExpressionFont = expression.FontFamily;
        var originalSourceFont = source.FontFamily;

        ScriptEditorFonts.ApplyTo(expression);
        ScriptEditorFonts.ApplyTo(source);

        if (ScriptEditorFonts.SelectedFamily is { } selected)
        {
            Assert.That(expression.FontFamily, Is.EqualTo(selected));
            Assert.That(source.FontFamily, Is.EqualTo(selected));
        }
        else
        {
            Assert.That(expression.FontFamily, Is.EqualTo(originalExpressionFont));
            Assert.That(source.FontFamily, Is.EqualTo(originalSourceFont));
        }
    }
}
