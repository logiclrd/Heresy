using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>C2(e): the *configured use* of a shared Filter and its tail are
/// distinct inspector fields from C2(c)'s shared-definition stage editing.
/// Probe actual per-use Apply, invalid/no-op suppression, compatible held
/// coefficient migration and structural definition removal.</summary>
[TestFixture]
public sealed class FmSynthFilterInstanceInspectorLivePcmTests
{
    [AvaloniaTest]
    public void PerUseOnePoleOverrideAndTailAdoptHeldPcmWhileClearingDefinitionRetainsOldVoice()
    {
        const int rate = 8000;
        DocumentWorkspace workspace = new();
        FilterDefinition shared = FilterDocumentEditor.CreateFilter(
            workspace, "C2(e) shared pole");
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(e) per-use Filter");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 190, 80, 80);
        int filterId = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 310, 80);
        FmSynthDocumentEditor.ConnectNodes(workspace, synth,
            oscillator, filterId, 0);
        FmSynthDocumentEditor.SetFilterConfiguration(workspace, synth, filterId,
            new FilterInstance(shared));
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, filterId);

        FilterDefinition oracleDefinition = new(shared.Id, "independent pole");
        FilterDefinition untouchedDefinition = new(shared.Id, "untouched pole");
        FilterInstance oracleInstance = new(oracleDefinition);
        FilterInstance uneditedInstance = new(untouchedDefinition);
        Resolver actualResolver = new(synth.Id), oracleResolver = new(synth.Id),
            originalResolver = new(synth.Id);
        FmSynthSound Author(FilterInstance instance, double tail, Resolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(node =>
                node.Id == filterId ? (FmSynthNode)new FmFilterNode(filterId,
                    oscillator, instance, tailSeconds: tail) : node), filterId),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound current = new(synth.Graph, actualResolver,
            soundResolver: actualResolver, enableLiveMultiExpansion: true);
        FmSynthSound independent = Author(oracleInstance, 0, oracleResolver);
        FmSynthSound untouched = Author(uneditedInstance, 0, originalResolver);
        actualResolver.Sound = current;
        oracleResolver.Sound = independent;
        originalResolver.Sound = untouched;
        RenderContext context = new(new RenderConfiguration(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        PlaybackSession Session(Resolver resolver) =>
            new(context, new NoteScheduleBuilder().Freeze(), resolver);
        PlaybackSession actual = Session(actualResolver);
        PlaybackSession oracle = Session(oracleResolver);
        PlaybackSession original = Session(originalResolver);
        foreach (PlaybackSession session in new[] { actual, oracle, original })
            session.ApplyLiveEvent(ChannelTarget.Physical(0),
                [new StartNoteCommand(synth.Id)]);
        float[] before = new float[120], referenceBefore = new float[120];
        actual.Render(0, before.Length, before);
        oracle.Render(0, referenceBefore.Length, referenceBefore);
        original.Render(0, 120, new float[120]);
        Assert.That(before, Is.EqualTo(referenceBefore).Within(1e-6f));
        PlaybackVoice held = actual.GetChannelState(0).CurrentVoice!;

        int published = 0, changed = 0;
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                FmSynthSound revised = new(synth.Graph, actualResolver,
                    soundResolver: actualResolver, enableLiveMultiExpansion: true);
                actual.AdoptLiveSynthParameters(synth.Id, current, revised);
                current = revised;
                actualResolver.Sound = revised;
                published++;
                return Task.CompletedTask;
            });
        StackPanel panel = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        Control Field(string label)
        {
            int index = panel.Children.Select((c, i) => (c, i))
                .Single(pair => pair.c is TextBlock text && text.Text == label).i;
            return panel.Children[index + 1];
        }
        void Enter(TextBox box, string text)
        {
            box.Text = text;
            box.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });
        }
        FilterInstanceEditorControl instanceEditor =
            panel.Children.OfType<FilterInstanceEditorControl>().Single();
        Dictionary<string, CheckBox> toggles =
            (Dictionary<string, CheckBox>)typeof(FilterInstanceEditorControl)
                .GetField("_overrideToggles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(instanceEditor)!;
        Dictionary<string, Control> fields =
            (Dictionary<string, Control>)typeof(FilterInstanceEditorControl)
                .GetField("_editors", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(instanceEditor)!;
        TextBox cutoff = (TextBox)fields["CutoffHz"];
        CheckBox overrideCutoff = toggles["CutoffHz"];
        void Apply() => typeof(FilterInstanceEditorControl)
            .GetMethod("ApplyDraft", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(instanceEditor, null);
        uint startRevision = workspace.Document.AudioRevision;
        uint startDocument = workspace.Document.DocumentRevision;

        overrideCutoff.IsChecked = true;
        cutoff.Text = "NaN";
        Apply();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startRevision),
                "an invalid per-use cutoff must not edit the Synth");
            Assert.That(published, Is.Zero);
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>()
                .Single().Configuration!.GetOverrides(1), Is.Empty);
        });

        cutoff.Text = "45";
        Apply();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startRevision + 1));
            Assert.That(published, Is.EqualTo(1),
                "two editor changed callbacks may occur, but publication is once per revision");
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>().Single()
                .Configuration!.GetOverrides(1)["CutoffHz"].NumericValue,
                Is.EqualTo(45));
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(held));
            Assert.That(held.Sound, Is.SameAs(current));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });
        Apply();
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startRevision + 1));
        Assert.That(published, Is.EqualTo(1));

        oracleInstance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(45));
        FmSynthSound newOracle = Author(oracleInstance, 0, oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, independent, newOracle);
        independent = newOracle;
        oracleResolver.Sound = independent;
        int frame = 120;
        void Compare(int count, bool shouldDiffer)
        {
            float[] observed = new float[count], expected = new float[count],
                originalOutput = new float[count];
            actual.Render(frame, count, observed);
            oracle.Render(frame, count, expected);
            original.Render(frame, count, originalOutput);
            for (int i = 0; i < count; i++)
                Assert.That(observed[i], Is.EqualTo(expected[i]).Within(4e-5f),
                    $"per-use OnePole editor PCM at frame {frame + i}");
            if (shouldDiffer)
                Assert.That(observed.Zip(originalOutput,
                    (a, b) => Math.Abs(a-b)).Max(), Is.GreaterThan(0.04),
                    "the per-use cutoff change must affect audible held PCM");
            frame += count;
        }
        Compare(900, true);

        TextBox tail = (TextBox)Field("Tail (seconds, nonnegative)");
        uint beforeTail = workspace.Document.AudioRevision;
        Enter(tail, "-0.5");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeTail),
            "invalid negative tail must be rejected atomically");
        Enter(tail, "0.05");
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>()
                .Single().TailSeconds, Is.EqualTo(0.05));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeTail + 1));
            Assert.That(published, Is.EqualTo(2));
            Assert.That(held.Sound, Is.SameAs(current));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });
        Enter(tail, "0.05");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeTail + 1));
        newOracle = Author(oracleInstance, 0.05, oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, independent, newOracle);
        independent = newOracle;
        oracleResolver.Sound = independent;
        Compare(900, true);

        // Selecting (None) changes the compiled Filter topology; it is not
        // a compatible parameter edit. The old held graph must remain audible
        // and be reported as an older generation, while new notes use bypass.
        StackPanel selectionRow = (StackPanel)Field("Shared Filter definition");
        ComboBox selection = (ComboBox)selectionRow.Children[0];
        object none = selection.Items.OfType<ComboBoxItem>()
            .Single(item => Equals(item.Content, "(None)"));
        FmSynthSound previous = (FmSynthSound)held.Sound;
        uint beforeSelection = workspace.Document.AudioRevision;
        selection.SelectedItem = none;
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>()
                .Single().Configuration, Is.Null);
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeSelection + 1));
            Assert.That(published, Is.EqualTo(3));
            Assert.That(held.Sound, Is.SameAs(previous));
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
        });
        // Old voice and untouched independent frozen stage history continue.
        float[] retained = new float[240], oldOracle = new float[240];
        actual.Render(frame, retained.Length, retained);
        oracle.Render(frame, oldOracle.Length, oldOracle);
        for (int i = 0; i < retained.Length; i++)
            Assert.That(retained[i], Is.EqualTo(oldOracle[i]).Within(4e-5f),
                $"structurally retired Filter graph must retain its IIR at {frame + i}");

        PlaybackSession fresh = Session(actualResolver);
        fresh.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(synth.Id)]);
        float[] bypass = new float[240];
        fresh.Render(0, bypass.Length, bypass);
        Assert.That(bypass.Zip(retained, (a, b) => Math.Abs(a-b)).Max(),
            Is.GreaterThan(0.04),
            "fresh NoteOn must use bypass, distinct from held old Filter state");
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startRevision + 3));
            Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(startDocument + 3));
            Assert.That(published, Is.EqualTo(3));
            Assert.That(changed, Is.GreaterThanOrEqualTo(3));
        });
    }

    private sealed class Resolver(ObjectId id)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public ISound? Sound { get; set; }
        public bool TryResolve(ObjectId requested, bool mixdown, out ISound? sound)
        {
            sound = requested == id && !mixdown ? Sound : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
