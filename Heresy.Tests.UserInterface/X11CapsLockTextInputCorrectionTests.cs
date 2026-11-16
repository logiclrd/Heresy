using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.UserInterface.Keyboard;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class X11CapsLockTextInputCorrectionTests
{
    private sealed class ModifierQuery : IX11KeyboardModifierQuery
    {
        public X11KeyboardModifiers? State { get; set; }
        public int Reads { get; private set; }
        public X11KeyboardModifiers? Read()
        {
            Reads++;
            return State;
        }
    }

    [TestCase(PhysicalKey.Quote, "\"", "'")]
    [TestCase(PhysicalKey.Comma, "<", ",")]
    [TestCase(PhysicalKey.Period, ">", ".")]
    [TestCase(PhysicalKey.Semicolon, ":", ";")]
    public void ExactlyFourReportedPunctuationKeysAreNormalized(
        PhysicalKey key, string bad, string good)
    {
        X11KeyboardModifiers caps = new(true, false);
        Assert.That(X11CapsLockTextInputCorrection.Correct(key, bad, caps),
            Is.EqualTo(good));
        Assert.That(X11CapsLockTextInputCorrection.Correct(key, bad,
            new X11KeyboardModifiers(false, false)), Is.EqualTo(bad));
        Assert.That(X11CapsLockTextInputCorrection.Correct(key, bad,
            new X11KeyboardModifiers(true, true)), Is.EqualTo(bad),
            "real or sticky Shift keeps its punctuation");
        Assert.That(X11CapsLockTextInputCorrection.Correct(key, bad, null),
            Is.EqualTo(bad));
    }

    [Test]
    public void DigitsLettersUnrelatedPunctuationAndImeTextAreNotRemapped()
    {
        X11KeyboardModifiers caps = new(true, false);
        Assert.That(X11CapsLockTextInputCorrection.Correct(
            PhysicalKey.Digit0, ")", caps), Is.EqualTo(")"));
        Assert.That(X11CapsLockTextInputCorrection.Correct(
            PhysicalKey.Digit2, "@", caps), Is.EqualTo("@"));
        Assert.That(X11CapsLockTextInputCorrection.Correct(
            PhysicalKey.A, "A", caps), Is.EqualTo("A"));
        Assert.That(X11CapsLockTextInputCorrection.Correct(
            PhysicalKey.Quote, "\">", caps), Is.EqualTo("\">"));
        Assert.That(X11CapsLockTextInputCorrection.Correct(
            PhysicalKey.Semicolon, "\"", caps), Is.EqualTo("\""));
    }

    [AvaloniaTest]
    public void TopLevelTunnelCorrectsPlainTextBoxesWithoutReplacingOrRethemingThem()
    {
        ModifierQuery query = new()
        {
            State = new X11KeyboardModifiers(true, false),
        };
        using X11CapsLockTextInputCorrection correction = new(query);
        TextBox first = new() { Text = "", Width = 150 };
        TextBox second = new() { Text = "", Width = 150 };
        StackPanel panel = new() { Spacing = 6 };
        panel.Children.Add(first);
        panel.Children.Add(second);
        Window host = new() { Width = 340, Height = 220, Content = panel };
        host.Show();
        try
        {
            Assert.That(first.GetType(), Is.EqualTo(typeof(TextBox)));
            Assert.That(second.GetType(), Is.EqualTo(typeof(TextBox)));
            Assert.That(first.IsVisible, Is.True);
            Assert.That(second.IsVisible, Is.True);

            void Input(TextBox box, PhysicalKey key, string text,
                KeyModifiers reported = KeyModifiers.Shift)
            {
                box.Focus();
                box.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.None,
                    PhysicalKey = key,
                    KeyModifiers = reported,
                });
                box.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = text,
                });
            }

            Input(first, PhysicalKey.Quote, "\"");
            Input(first, PhysicalKey.Comma, "<");
            Input(first, PhysicalKey.Period, ">");
            Input(first, PhysicalKey.Semicolon, ":");
            Assert.That(first.Text, Is.EqualTo("',.;"));
            Assert.That(first.CaretIndex, Is.EqualTo(4));

            // All stock TextBox instances, not only Synth fields, share
            // the same global hook without requiring a custom template.
            Input(second, PhysicalKey.Period, ">");
            Assert.That(second.Text, Is.EqualTo("."));
            Assert.That(first.Text, Is.EqualTo("',.;"));

            // The preceding physical key must agree with the character;
            // digits on Xephyr are unaffected in the observed reproduction.
            int beforeNormalKey = query.Reads;
            Input(second, PhysicalKey.Digit2, "2");
            Input(second, PhysicalKey.A, "A");
            Assert.That(second.Text, Is.EqualTo(".2A"));
            Assert.That(query.Reads, Is.EqualTo(beforeNormalKey),
                "native XKB is queried only for the four suspect glyphs");

            // Native Shift, Caps Off and an unavailable state never rewrite.
            query.State = new X11KeyboardModifiers(true, true);
            Input(second, PhysicalKey.Semicolon, ":");
            query.State = new X11KeyboardModifiers(false, false);
            Input(second, PhysicalKey.Comma, "<");
            query.State = null;
            Input(second, PhysicalKey.Quote, "\"");
            Assert.That(second.Text, Is.EqualTo(".2A:<\""));

            query.State = new X11KeyboardModifiers(true, false);
            Input(second, PhysicalKey.Quote, "\"", KeyModifiers.Control | KeyModifiers.Alt);
            Assert.That(second.Text, Is.EqualTo(".2A:<\"\""),
                "AltGr / command modifiers must not be corrected");

            // A multi-character composition is not rewritten.
            Input(second, PhysicalKey.Quote, "\">");
            Assert.That(second.Text, Is.EqualTo(".2A:<\"\"\">"));
        }
        finally
        {
            host.Close();
        }
    }
}
