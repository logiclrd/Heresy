using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptVirtualControlTests
{
    private const string Source = """
        NoteVirtual(0, 7U, _O(17));
        VolumeVirtual(0.25, 7U, 0.25);
        SourceFrameVirtual(0.5, 7U, 256, timeOffsetSeconds: 0.01);
        DisplacementVirtual(0.75, 7U, NoteDisplacementAction.Continue);
        PastVirtual(1, 7U, TrackerPastNoteAction.Off);
        DisplacementVirtual(1.25, 7U, NoteDisplacementAction.Cut);
        DisplacementVirtual(1.5, 7U, NoteDisplacementAction.Off);
        DisplacementVirtual(1.75, 7U, NoteDisplacementAction.Fade);
        PastVirtual(2, 7U, TrackerPastNoteAction.Cut);
        PastVirtual(2.25, 7U, TrackerPastNoteAction.Fade);
        """;

    [Test]
    public void EagerAndStreamingScriptsEmitTheSameTypedVirtualControls()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Controls")
        {
            RowCount = 3, ChannelCount = 1, Source = Source,
        };
        var eager = ScriptCompiler.CompilePattern(definition);
        var streaming = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.Multiple(() =>
        {
            Assert.That(eager.Success, Is.True,
                string.Join("; ", eager.Diagnostics.Select(x => x.Message)));
            Assert.That(streaming.Success, Is.True,
                string.Join("; ", streaming.Diagnostics.Select(x => x.Message)));
        });
        NoteScheduleBuilder output = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), output, out _);
        NoteEvent[] expected = output.Freeze().ToArray();
        NoteEvent[] actual = streaming.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Length, Is.EqualTo(10));
            Assert.That(actual.Select(e => e.Target),
                Is.All.EqualTo(ChannelTarget.Virtual(7U)));
            Assert.That(actual.Select(e => e.Offset),
                Is.EqualTo(expected.Select(e => e.Offset)));
            Assert.That(actual.Select(e => e.Commands.Single()),
                Is.EqualTo(expected.Select(e => e.Commands.Single())));
            Assert.That(actual[1].Commands.Single(),
                Is.EqualTo(new SetNoteVolumeCommand(0.25)));
            Assert.That(actual[2].Commands.Single(),
                Is.EqualTo(new SetSourceFrameOffsetCommand(256)));
            Assert.That(actual[2].Offset.TimeOffset,
                Is.EqualTo(TimeSpan.FromMilliseconds(10)));
            Assert.That(actual.Skip(3).Select(e => e.Commands.Single()),
                Is.EqualTo(new NoteCommand[]
                {
                    new SetCurrentVoiceDisplacementActionCommand(NoteDisplacementAction.Continue),
                    new ApplyPastNoteActionCommand(TrackerPastNoteAction.Off),
                    new SetCurrentVoiceDisplacementActionCommand(NoteDisplacementAction.Cut),
                    new SetCurrentVoiceDisplacementActionCommand(NoteDisplacementAction.Off),
                    new SetCurrentVoiceDisplacementActionCommand(NoteDisplacementAction.Fade),
                    new ApplyPastNoteActionCommand(TrackerPastNoteAction.Cut),
                    new ApplyPastNoteActionCommand(TrackerPastNoteAction.Fade),
                }));
        });
    }

    [Test]
    public void EveryVirtualControlSuspendsTheStreamingScriptBeforeItsNextStatement()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Laziness")
        {
            RowCount = 2, ChannelCount = 1,
            Source = """
                VolumeVirtual(0, 7U, 0.5);
                SourceFrameVirtual(0.25, 7U, 300);
                DisplacementVirtual(0.5, 7U, NoteDisplacementAction.Fade);
                PastVirtual(0.75, 7U, TrackerPastNoteAction.Cut);
                """,
        };
        var result = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(result.Success, Is.True,
            string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        using IEnumerator<RawPatternStep> iter =
            result.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
        Type[] types = [
            typeof(SetNoteVolumeCommand),
            typeof(SetSourceFrameOffsetCommand),
            typeof(SetCurrentVoiceDisplacementActionCommand),
            typeof(ApplyPastNoteActionCommand),
        ];
        foreach (Type type in types)
        {
            Assert.That(iter.MoveNext(), Is.True);
            Assert.That(iter.Current, Is.TypeOf<RawPatternStep.Emit>());
            Assert.That(((RawPatternStep.Emit)iter.Current).Note.Commands.Single().GetType(),
                Is.EqualTo(type));
        }
        Assert.That(iter.MoveNext(), Is.True);
        Assert.That(iter.Current, Is.TypeOf<RawPatternStep.Advance>());
        Assert.That(iter.MoveNext(), Is.False);
    }

    [TestCase("VolumeVirtual(0, 7U, -0.1);")]
    [TestCase("VolumeVirtual(0, 7U, Math.Sqrt(-1));")]
    [TestCase("VolumeVirtual(0, 7U, 1.1);")]
    [TestCase("SourceFrameVirtual(0, 7U, -1);")]
    [TestCase("SourceFrameVirtual(1.1, 7U, 0);")]
    [TestCase("DisplacementVirtual(0, 7U, (NoteDisplacementAction)99);")]
    [TestCase("PastVirtual(0, 7U, (TrackerPastNoteAction)99);")]
    [TestCase("PastVirtual(0, 7U, TrackerPastNoteAction.Cut, timeOffsetSeconds: 1e300);")]
    public void InvalidHelperArgumentsFailBeforeEmitting(string source)
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Invalid")
        {
            RowCount = 1, ChannelCount = 1, Source = source,
        };
        var compiled = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(x => x.Message)));
        using IEnumerator<RawPatternStep> iter =
            compiled.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
        Assert.Throws<ArgumentOutOfRangeException>(() => iter.MoveNext());
    }

    [TestCase("for (int i = 0; i < 1; VolumeVirtual(i, 7U, 0.5)) { }")]
    [TestCase("for (int i = 0; i < 1; SourceFrameVirtual(i, 7U, 10)) { }")]
    [TestCase("for (int i = 0; i < 1; DisplacementVirtual(i, 7U, NoteDisplacementAction.Continue)) { }")]
    [TestCase("for (int i = 0; i < 1; PastVirtual(i, 7U, TrackerPastNoteAction.Cut)) { }")]
    public void VirtualControlsAreOnlyValidAsDirectScriptStatements(string source)
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Nested")
        {
            RowCount = 2, Source = source,
        };
        var compiled = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.Diagnostics.Any(d => d.Code == "HRS2001"
            && d.Message.Contains("direct statements", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public void CoreTimelineStillRejectsPhysicalOnlyAndBroadcastOnlyMisuses()
    {
        NoteEvent[] unsupported = [
            new(new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 0),
                ChannelTarget.Virtual(7U), [new ApplyTrackerTempoCommand(125)]),
            new(new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 0),
                ChannelTarget.AllVirtualInScope,
                [new SetCurrentVoiceDisplacementActionCommand(
                    NoteDisplacementAction.Continue)]),
        ];
        foreach (NoteEvent note in unsupported)
        {
            SequencingContext context = new();
            using IncrementalPatternTimeline timeline = new(context);
            timeline.Add(new TestSource(note), 1, context);
            Assert.Throws<NotSupportedException>(() =>
            {
                while (timeline.TryStep(out _)) { }
            });
        }
    }

    private sealed class TestSource(NoteEvent note) : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            yield return new RawPatternStep.Emit(note);
        }
    }
}
