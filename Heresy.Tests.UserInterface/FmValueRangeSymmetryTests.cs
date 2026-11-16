using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Images;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmValueRangeSymmetryTests
{
    [Test]
    public void ExactRecenterAndBothLinkedEditDirectionsAreShared()
    {
        Assert.That(ValueRangeSymmetry.IsSymmetric(-1, 1), Is.True);
        Assert.That(ValueRangeSymmetry.IsSymmetric(-1, 2), Is.False);
        Assert.That(ValueRangeSymmetry.Center(-3, 5),
            Is.EqualTo((-4.0, 4.0)));
        Assert.That(ValueRangeSymmetry.Center(5, -3),
            Is.EqualTo((4.0, -4.0)),
            "Noise/Oscillator may use inverted ranges");
        Assert.That(ValueRangeSymmetry.SetMinimum(-0.7, 3, true),
            Is.EqualTo((-0.7, 0.7)));
        Assert.That(ValueRangeSymmetry.SetMaximum(2, 1.5, true),
            Is.EqualTo((-1.5, 1.5)));
        Assert.That(ValueRangeSymmetry.SetMinimum(-0.7, 3, false),
            Is.EqualTo((-0.7, 3.0)));
        Assert.That(ValueRangeSymmetry.SetMaximum(2, 1.5, false),
            Is.EqualTo((2.0, 1.5)));
        Assert.Throws<ArgumentException>(() =>
            ValueRangeSymmetry.Center(-double.MaxValue, double.MaxValue));
    }

    [AvaloniaTest]
    public void ChainToggleBackgroundMatchesHostAndCornerPixelsRemainClickable()
    {
        // A transparent chain SVG has no ink at the button corners.
        // Those corners must still receive the same pointer input as the
        // icon itself, while blending into the real hosting window.
        ValueRangeLinkToggle toggle = new(initiallyLinked: true);
        SolidColorBrush background = new(Colors.LightSteelBlue);
        Window host = new()
        {
            Width = 240,
            Height = 150,
            Background = background,
            Content = new StackPanel { Children = { toggle } },
        };
        host.Show();
        try
        {
            Assert.That(toggle.Background, Is.SameAs(background));
            Assert.That(((SolidColorBrush)toggle.Background!).Color.A,
                Is.EqualTo(byte.MaxValue), "hit-test background is opaque");
            Assert.That(toggle.Bounds.Width, Is.GreaterThan(20));
            Assert.That(toggle.Bounds.Height, Is.GreaterThan(20));

            void ClickCorner(double x, double y)
            {
                Point pt = toggle.TranslatePoint(new Point(x, y), host)!.Value;
                host.MouseMove(pt);
                host.MouseDown(pt, MouseButton.Left);
                host.MouseUp(pt, MouseButton.Left);
            }
            ClickCorner(2, 2);
            Assert.That(toggle.IsChecked, Is.False,
                "top-left corner outside the 20-pixel chain artwork must toggle");
            ClickCorner(toggle.Bounds.Width - 2, toggle.Bounds.Height - 2);
            Assert.That(toggle.IsChecked, Is.True,
                "bottom-right blank corner must also toggle");

            // Using the actual window background binding means later theme
            // changes cannot leave a contrasting opaque tile behind.
            SolidColorBrush changed = new(Colors.DarkSlateGray);
            host.Background = changed;
            Assert.That(toggle.Background, Is.SameAs(changed));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void NoiseLinkReflectsIconAndCommitsBothBoundsExactlyOncePerEdit()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Noise Link");
        int id = FmSynthDocumentEditor.AddNoiseNode(
            workspace, synth, 130, 70);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        int changed = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => {}, _ => changed++);
        StackPanel row = RangeRow(editor);
        TextBox minimum = Bound(row, "Vmin");
        TextBox maximum = Bound(row, "Vmax");
        ValueRangeLinkToggle link = row.Children.OfType<ValueRangeLinkToggle>()
            .Single();
        Assert.That(link.IsChecked, Is.True);
        Assert.That(link.Content, Is.TypeOf<ChainLinked>());
        uint revision = workspace.Document.AudioRevision;
        FmNoiseNode Current() => synth.Graph.Nodes.OfType<FmNoiseNode>().Single();

        Enter(minimum, "-0.25");
        Assert.That(Current().Minimum, Is.EqualTo(-0.25));
        Assert.That(Current().Maximum, Is.EqualTo(0.25));
        Assert.That(maximum.Text, Is.EqualTo("0.25"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1),
            "one linked edit is one atomic graph publication");
        // Paired field has a newly accepted baseline: focus loss cannot
        // accidentally publish the old Vmax.
        maximum.RaiseEvent(new Avalonia.Input.FocusChangedEventArgs(
            InputElement.LostFocusEvent));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        maximum.Text = "bad draft";
        maximum.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
        });
        Assert.That(maximum.Text, Is.EqualTo("0.25"));

        Enter(maximum, "0.8");
        Assert.That(Current().Minimum, Is.EqualTo(-0.8));
        Assert.That(Current().Maximum, Is.EqualTo(0.8));
        Assert.That(minimum.Text, Is.EqualTo("-0.8"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));

        link.IsChecked = false;
        Assert.That(link.Content, Is.TypeOf<ChainUnlinked>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2),
            "unlinking does not mutate the model");
        Enter(maximum, "2.2");
        Assert.That(Current().Minimum, Is.EqualTo(-0.8));
        Assert.That(Current().Maximum, Is.EqualTo(2.2));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 3));

        link.IsChecked = true;
        Assert.That(link.Content, Is.TypeOf<ChainLinked>());
        Assert.That(Current().Minimum, Is.EqualTo(-1.5));
        Assert.That(Current().Maximum, Is.EqualTo(1.5),
            "turning on re-centers to half the old span, not the midpoint");
        Assert.That(minimum.Text, Is.EqualTo("-1.5"));
        Assert.That(maximum.Text, Is.EqualTo("1.5"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 4));
        Assert.That(changed, Is.EqualTo(4));
        StackPanel reopened = RangeRow(
            new FmSynthEditorControl(workspace, synth, () => {}, _ => {}));
        Assert.That(reopened.Children.OfType<ValueRangeLinkToggle>()
            .Single().IsChecked, Is.True);
    }

    [AvaloniaTest]
    public void OscillatorLinkAllowsInvertedValuesAndReopensWithCorrectState()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Oscillator Link");
        int id = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine,
            120, 90, 85, minimum: -0.25, maximum: 0.9);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthEditorControl editor = new(workspace, synth, () => {}, _ => {});
        StackPanel row = RangeRow(editor);
        TextBox minimum = Bound(row, "Vmin");
        TextBox maximum = Bound(row, "Vmax");
        ValueRangeLinkToggle link = row.Children.OfType<ValueRangeLinkToggle>()
            .Single();
        Assert.That(link.IsChecked, Is.False,
            "asymmetric saved bounds start unlinked");
        uint revision = workspace.Document.AudioRevision;
        link.IsChecked = true;
        FmOscillatorNode Current() =>
            synth.Graph.Nodes.OfType<FmOscillatorNode>().Single();
        Assert.That(Current().Minimum, Is.EqualTo(-0.575));
        Assert.That(Current().Maximum, Is.EqualTo(0.575));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

        Enter(minimum, "0.2");
        Assert.That(Current().Minimum, Is.EqualTo(0.2));
        Assert.That(Current().Maximum, Is.EqualTo(-0.2),
            "both positive and inverted ranges are valid for Oscillators");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
        Enter(maximum, "0.7");
        Assert.That(Current().Minimum, Is.EqualTo(-0.7));
        Assert.That(Current().Maximum, Is.EqualTo(0.7));
        Assert.That(minimum.Text, Is.EqualTo("-0.7"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 3));
        link.IsChecked = false;
        Enter(minimum, "-0.5");
        Assert.That(Current().Minimum, Is.EqualTo(-0.5));
        Assert.That(Current().Maximum, Is.EqualTo(0.7));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 4));
        StackPanel reopened = RangeRow(
            new FmSynthEditorControl(workspace, synth, () => {}, _ => {}));
        Assert.That(reopened.Children.OfType<ValueRangeLinkToggle>()
            .Single().IsChecked, Is.False);
    }

    private static StackPanel RangeRow(FmSynthEditorControl editor)
    {
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        return inspector.Children.OfType<StackPanel>()
            .Single(x => x.Name == "ValueRangeFields");
    }

    private static TextBox Bound(StackPanel range, string name)
        => range.Children.OfType<StackPanel>().Single(x =>
            x.Children.OfType<TextBlock>().Any(t => t.Text == name))
            .Children.OfType<TextBox>().Single();

    private static void Enter(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
    }
}
