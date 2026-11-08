using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using AwesomeAssertions;

using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PianoPreviewCapsLockModeTests
{
    private sealed class Query : ICapsLockQuery
    {
        public bool? Value { get; set; }
        public bool? IsCapsLockOn() => Value;
    }

    [Test]
    public void UnknownNativeStateStartsInHoldAndEachActualPressChangesLocalMode()
    {
        Query query = new() { Value = null };
        PianoPreviewCapsLockMode mode = new(query);
        mode.IsNativeStateAvailable.Should().BeFalse();
        mode.IsLatch.Should().BeFalse();

        mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeTrue();
        mode.Refresh(); // focus leaves and returns without an OS query
        mode.IsLatch.Should().BeTrue();

        mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeFalse();
    }

    [Test]
    public void KnownNativeStateIsAuthoritativeButUnknownNeverResetsMode()
    {
        Query query = new() { Value = true };
        PianoPreviewCapsLockMode mode = new(query);
        mode.IsNativeStateAvailable.Should().BeTrue();
        mode.IsLatch.Should().BeTrue();

        // An OS query is authoritative, even if a Caps Lock event is
        // already dispatched but the platform reports no state transition.
        mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeTrue();
        query.Value = null;
        mode.Refresh();
        mode.IsNativeStateAvailable.Should().BeFalse();
        mode.IsLatch.Should().BeTrue();

        mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeFalse();

        // Return to a working native query when the window is reactivated.
        query.Value = true;
        mode.Refresh();
        mode.IsNativeStateAvailable.Should().BeTrue();
        mode.IsLatch.Should().BeTrue();
        query.Value = false;
        mode.Refresh();
        mode.IsLatch.Should().BeFalse();
    }

    [Test]
    public void XephyrStyleReleaseKeydownPairIsNotASecondToggle()
    {
        PianoPreviewCapsLockMode mode = new(new Query());
        HeldPhysicalKeyRepeatState repeats = new();
        if (repeats.KeyDown(PhysicalKey.CapsLock))
            mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeTrue();

        for (long time = 10; time <= 210; time += 20)
        {
            repeats.KeyUp(PhysicalKey.CapsLock, time).Should().BeTrue();
            repeats.CollectReleased(time + 1).Should().BeEmpty();
            if (repeats.KeyDown(PhysicalKey.CapsLock))
                mode.OnCapsLockPressed();
        }
        mode.IsLatch.Should().BeTrue();

        repeats.KeyUp(PhysicalKey.CapsLock, 300).Should().BeTrue();
        repeats.CollectReleased(339).Should().BeEmpty();
        repeats.CollectReleased(340).Should().Equal(PhysicalKey.CapsLock);
        if (repeats.KeyDown(PhysicalKey.CapsLock))
            mode.OnCapsLockPressed();
        mode.IsLatch.Should().BeFalse();
    }

    [AvaloniaTest]
    public void CapsLockAnywhereInOwningWindowUpdatesPreviewAndDoesNotConsumeEvent()
    {
        Query unavailable = new();
        List<PianoPreviewEvent> events = [];
        PianoTestControl test = new(
            evt =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            },
            () => false,
            () => Task.CompletedTask,
            unavailable);
        TextBox otherField = new() { Text = "another focused input" };
        StackPanel stack = new();
        stack.Children.Add(test);
        stack.Children.Add(otherField);
        Window window = new() { Content = stack };
        try
        {
            window.Show();
            Border testArea = Field<Border>(test, "_testArea");
            TextBlock modeLabel = Field<TextBlock>(test, "_mode");

            modeLabel.Text.Should().Be("Mode: Hold (locally tracked)");
            otherField.Focus();

            KeyEventArgs first = KeyEvent(InputElement.KeyDownEvent,
                PhysicalKey.CapsLock, Key.CapsLock);
            otherField.RaiseEvent(first);
            first.Handled.Should().BeFalse("Caps Lock is observed, not captured");
            modeLabel.Text.Should().Be("Mode: Latch (locally tracked)");

            // A Xephyr release/down repeat pair crosses a focus change.
            otherField.RaiseEvent(KeyEvent(InputElement.KeyUpEvent,
                PhysicalKey.CapsLock, Key.CapsLock));
            testArea.Focus();
            testArea.RaiseEvent(KeyEvent(InputElement.KeyDownEvent,
                PhysicalKey.CapsLock, Key.CapsLock));
            modeLabel.Text.Should().Be("Mode: Latch (locally tracked)",
                "the repeat filter is not reset by Test focus changes");

            // The mode observed in another input must be used by Test.
            testArea.RaiseEvent(KeyEvent(InputElement.KeyDownEvent,
                PhysicalKey.Z, Key.Z));
            events.Should().ContainSingle()
                .Which.Should().BeOfType<PianoPreviewNoteOn>()
                .Which.SuppressNeighbours.Should().BeTrue();

            otherField.Focus();
            modeLabel.Text.Should().Be("Mode: Latch (locally tracked)");

            // An event previously marked handled is still observed.
            // Clear only Caps Lock repeat state to simulate a newly
            // confirmed physical press, not a focus change.
            Field<HeldPhysicalKeyRepeatFilter>(test, "_capsRepeats").Clear();
            KeyEventArgs handled = KeyEvent(InputElement.KeyDownEvent,
                PhysicalKey.CapsLock, Key.CapsLock);
            handled.Handled = true;
            otherField.RaiseEvent(handled);
            modeLabel.Text.Should().Be("Mode: Hold (locally tracked)");
        }
        finally
        {
            window.Close();
        }
    }

    private static KeyEventArgs KeyEvent(
        RoutedEvent routedEvent, PhysicalKey physical, Key logical)
        => new()
        {
            RoutedEvent = routedEvent,
            PhysicalKey = physical,
            Key = logical,
            KeyModifiers = KeyModifiers.None,
        };

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
}
