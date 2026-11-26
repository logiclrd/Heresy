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
        using var note = NoteVirtual(0, _O(17));
        note.Volume(0.25, 0.25);
        note.SourceFrame(0.5, 256, timeOffsetSeconds: 0.01);
        note.Displacement(0.75, NoteDisplacementAction.Continue);
        note.Past(1, TrackerPastNoteAction.Off);
        note.Displacement(1.25, NoteDisplacementAction.Cut);
        note.Displacement(1.5, NoteDisplacementAction.Off);
        note.Displacement(1.75, NoteDisplacementAction.Fade);
        note.Past(2, TrackerPastNoteAction.Cut);
        note.Past(2.25, TrackerPastNoteAction.Fade);
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
                Is.All.EqualTo(ChannelTarget.Virtual(new VirtualChannelIdentity(0, 1))));
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
                using var note = NoteVirtual(0, _O(17));
                note.Volume(0, 0.5);
                note.SourceFrame(0.25, 300);
                note.Displacement(0.5, NoteDisplacementAction.Fade);
                note.Past(0.75, TrackerPastNoteAction.Cut);
                """,
        };
        var result = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(result.Success, Is.True,
            string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        using IEnumerator<RawPatternStep> iter =
            result.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
        Type[] types = [
            typeof(StartNoteCommand),
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
        Assert.That(iter.Current, Is.TypeOf<RawPatternStep.ReleaseVirtualChannel>());
        Assert.That(iter.MoveNext(), Is.True);
        Assert.That(iter.Current, Is.TypeOf<RawPatternStep.Advance>());
        Assert.That(iter.MoveNext(), Is.False);
    }

    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Volume(0, -0.1);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Volume(0, Math.Sqrt(-1));")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Volume(0, 1.1);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.SourceFrame(0, -1);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.SourceFrame(1.1, 0);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Displacement(0, (NoteDisplacementAction)99);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Past(0, (TrackerPastNoteAction)99);")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Past(0, TrackerPastNoteAction.Cut, timeOffsetSeconds: 1e300);")]
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
        Assert.Throws<ArgumentOutOfRangeException>(() => { while (iter.MoveNext()) { } });
    }

    [TestCase("using var note = NoteVirtual(0, _O(17)); for (int i = 0; i < 1; note.Volume(i, 0.5)) { }")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); for (int i = 0; i < 1; note.SourceFrame(i, 10)) { }")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); for (int i = 0; i < 1; note.Displacement(i, NoteDisplacementAction.Continue)) { }")]
    [TestCase("using var note = NoteVirtual(0, _O(17)); for (int i = 0; i < 1; note.Past(i, TrackerPastNoteAction.Cut)) { }")]
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
