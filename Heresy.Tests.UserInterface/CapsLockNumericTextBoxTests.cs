using System;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.UserInterface.Keyboard;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class CapsLockNumericTextBoxTests
{
    private sealed class ModifierQuery : IX11KeyboardModifierQuery
    {
        public X11KeyboardModifiers? State { get; set; }

        public X11KeyboardModifiers? Read() => State;
    }

    [TestCase(PhysicalKey.Digit0, ")", "0")]
    [TestCase(PhysicalKey.Digit1, "!", "1")]
    [TestCase(PhysicalKey.Digit2, "@", "2")]
    [TestCase(PhysicalKey.Digit3, "#", "3")]
    [TestCase(PhysicalKey.Digit4, "$", "4")]
    [TestCase(PhysicalKey.Digit5, "%", "5")]
    [TestCase(PhysicalKey.Digit6, "^", "6")]
    [TestCase(PhysicalKey.Digit7, "&", "7")]
    [TestCase(PhysicalKey.Digit8, "*", "8")]
    [TestCase(PhysicalKey.Digit9, "(", "9")]
    [TestCase(PhysicalKey.Period, ">", ".")]
    [TestCase(PhysicalKey.Minus, "_", "-")]
    [TestCase(PhysicalKey.Equal, "+", "=")]
    [TestCase(PhysicalKey.Comma, "<", ",")]
    [TestCase(PhysicalKey.Slash, "?", "/")]
    public void CorrectsOnlyMatchingPhysicallyUnshiftedKeysWhenCapsIsLocked(
        PhysicalKey key, string wrong, string right)
    {
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(key, wrong,
            new X11KeyboardModifiers(true, false)), Is.EqualTo(right));
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(key, wrong,
            new X11KeyboardModifiers(true, true)), Is.EqualTo(wrong),
            "genuinely depressed Shift must preserve shifted punctuation");
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(key, wrong,
            new X11KeyboardModifiers(false, false)), Is.EqualTo(wrong),
            "ordinary Shift typing without Caps must remain unchanged");
    }

    [Test]
    public void NonmatchingPhysicalKeysAndImeStringsRemainUntouched()
    {
        X11KeyboardModifiers caps = new(true, false);
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(
            PhysicalKey.Digit2, "!", caps), Is.EqualTo("!"));
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(
            PhysicalKey.A, "A", caps), Is.EqualTo("A"),
            "Caps must still uppercase letters");
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(
            PhysicalKey.Digit0, "0.2", caps), Is.EqualTo("0.2"),
            "multi-character IME input must not be modified");
        Assert.That(CapsLockNumericTextBox.RestoreUnshifted(
            PhysicalKey.NumPad2, "@", caps), Is.EqualTo("@"),
            "the numeric keypad is independent of the XKB US number row");
    }

    [AvaloniaTest]
    public void TextInputInterceptionRepairsXephyrSequenceWithoutEditingTextBoxSemantics()
    {
        ModifierQuery query = new()
        {
            State = new X11KeyboardModifiers(CapsLocked: true, ShiftActive: false),
        };
        CapsLockNumericTextBox box = new(query)
        {
            Width = 170, Text = "",
        };
        Window host = new() { Width = 290, Height = 180, Content = box };
        host.Show();
        try
        {
            box.Focus();
            void Input(PhysicalKey key, string text,
                KeyModifiers modifiers = KeyModifiers.Shift)
            {
                box.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.None,
                    PhysicalKey = key,
                    KeyModifiers = modifiers,
                });
                box.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = text,
                });
            }
            // Xephyr's erroneous translated values for 0.2 with Latch.
            Input(PhysicalKey.Digit0, ")");
            Input(PhysicalKey.Period, ">");
            Input(PhysicalKey.Digit2, "@");
            Assert.That(box.Text, Is.EqualTo("0.2"));
            Assert.That(box.CaretIndex, Is.EqualTo(3),
                "the normal TextBox text insertion path advances the caret");

            query.State = new X11KeyboardModifiers(true, true);
            Input(PhysicalKey.Digit2, "@");
            Assert.That(box.Text, Is.EqualTo("0.2@"),
                "real Shift is not suppressed by the Caps lock workaround");

            query.State = new X11KeyboardModifiers(false, false);
            Input(PhysicalKey.Digit0, ")");
            Assert.That(box.Text, Is.EqualTo("0.2@)"),
                "Caps off input is never rewritten");
            query.State = null;
            Input(PhysicalKey.Digit2, "@");
            Assert.That(box.Text, Is.EqualTo("0.2@)@"),
                "an unavailable native X11 state must not reinterpret text");

            // No associated KeyDown means paste/IME should not be mapped.
            query.State = new X11KeyboardModifiers(true, false);
            box.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = ")>@",
            });
            Assert.That(box.Text, Is.EqualTo("0.2@)@)>@"));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void ControlAndAltGrSequencesAreNeverRewritten()
    {
        ModifierQuery query = new()
        {
            State = new X11KeyboardModifiers(true, false),
        };
        CapsLockNumericTextBox box = new(query) { Text = "" };
        Window host = new() { Width = 260, Height = 140, Content = box };
        host.Show();
        try
        {
            box.Focus();
            box.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.None, PhysicalKey = PhysicalKey.Digit2,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt,
            });
            box.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = "@",
            });
            Assert.That(box.Text, Is.EqualTo("@"));
        }
        finally { host.Close(); }
    }
}
