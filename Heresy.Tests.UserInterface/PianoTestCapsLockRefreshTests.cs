using System;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PianoTestCapsLockRefreshTests
{
    private sealed class FakeCapsLockQuery : ICapsLockQuery
    {
        public bool? Current { get; set; }

        public bool? IsCapsLockOn() => Current;
    }

    [AvaloniaTest]
    public void NativeCapsLockReleasedToOffRefreshesModeWithoutAnyPianoKey()
    {
        // An X11 backend can deliver Caps Lock KeyDown before the server's
        // updated locked-modifier state is observable to a separate XKB query.
        // KeyUp must re-read that state even when no piano key follows.
        FakeCapsLockQuery query = new() { Current = true };
        PianoTestControl control = new(
            _ => Task.CompletedTask,
            () => false,
            () => Task.CompletedTask,
            query);
        Border area = Field<Border>(control, "_testArea");
        TextBlock label = Field<TextBlock>(control, "_mode");
        Window window = new() { Content = control };
        try
        {
            // The newer preview control observes Caps Lock on the owning
            // window, even when another child is focused.
            window.Show();
            label.Text.Should().Be("Mode: Latch");

            area.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.CapsLock,
                PhysicalKey = PhysicalKey.CapsLock,
            });
            label.Text.Should().Be("Mode: Latch",
                "the native query has not yet observed the transition");

            query.Current = false;
            area.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = Key.CapsLock,
                PhysicalKey = PhysicalKey.CapsLock,
            });
            label.Text.Should().Be("Mode: Hold",
                "Caps Lock KeyUp must refresh the visible state proactively");
        }
        finally
        {
            window.Close();
        }
    }

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
}
