using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

/// <summary>Production restricted Roslyn script -> lazy raw iterator ->
/// shared-tick resolver for already-renderable physical voice controls.
/// No script may advance the next statement before the previous helper yields.
/// </summary>
[TestFixture]
public sealed class IncrementalDirectControlScriptTests
{
    [Test]
    public void DirectPhysicalControlsStreamAtActualSharedTickDeadlines()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Direct controls")
        {
            RowCount = 4,
            ChannelCount = 1,
            Source = """
                Note(0, 0, _O(17));
                Pan(0.5, 0, -0.5, 0.25);
                Filter(1, 0, 0.2, 0.4);
                FilterCutoff(1.5, 0, 0.6);
                FilterResonance(2, 0, 0.7);
                Seek(2.5, 0, 0.075);
                Surround(3, 0, true);
                """,
        };
        var compiled = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(x => x.Message)));
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(compiled.Program!, definition.RowCount, context);
        NoteEvent[] notes = Drain(timeline);
        Assert.Multiple(() =>
        {
            Assert.That(notes, Has.Length.EqualTo(7));
            Assert.That(notes.Select(x => x.Offset.TimeOffset.TotalSeconds),
                Is.EqualTo(new[] { 0.0, 0.06, 0.12, 0.18,
                    0.24, 0.3, 0.36 }).Within(1e-6));
            Assert.That(notes.Select(x => x.Commands.Count),
                Is.All.EqualTo(1));
            Assert.That(notes.Select(x => x.Commands[0]),
                Is.EqualTo(new NoteCommand[]
                {
                    new StartNoteCommand((ObjectId)17U),
                    new SetSpatialPositionCommand(new Vector3(-0.5f, 0.25f, 0)),
                    new SetResonantFilterCommand(0.2, 0.4),
                    new SetResonantFilterCutoffCommand(0.6),
                    new SetResonantFilterResonanceCommand(0.7),
                    new SetPlaybackOffsetCommand(TimeSpan.FromMilliseconds(75)),
                    new SetSurroundCommand(true),
                }));
        });
    }

    [Test]
    public void FixedWallFilterExecutesAfterConcurrentGlobalTempoChange()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Deferred filter")
        {
            RowCount = 2,
            ChannelCount = 1,
            Source = """
                Tempo(0, 250);
                Filter(0.5, 0, 0.25, 0.5, 0.05);
                """,
        };
        var compiled = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compiled.Success, Is.True);
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(compiled.Program!, definition.RowCount, context);
        NoteEvent[] notes = Drain(timeline);
        Assert.Multiple(() =>
        {
            Assert.That(notes.Select(n => n.Offset.TimeOffset.TotalSeconds),
                Is.EqualTo(new[] { 0.0, 0.08 }).Within(1e-6));
            Assert.That(notes[0].Commands.Single(),
                Is.EqualTo(new SetTempoCommand(250)));
            Assert.That(notes[1].Commands.Single(),
                Is.EqualTo(new SetResonantFilterCommand(0.25, 0.5)));
            Assert.That(context.State.Tempo, Is.EqualTo(250));
        });
    }

    [Test]
    public void ScriptHelperValidationIsLazyAndErrorsBeforeEmittingAnInvalidCommand()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Lazy validation")
        {
            RowCount = 2,
            ChannelCount = 1,
            Source = """
                Filter(0, 0, 0.25, 0.5);
                Pan(1, 99, 0);
                """,
        };
        var compiled = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compiled.Success, Is.True);
        using IEnumerator<RawPatternStep> iter =
            compiled.Program!.EnumerateRawSteps(new SequencingContext())
                .GetEnumerator();
        Assert.That(iter.MoveNext(), Is.True);
        Assert.That(((RawPatternStep.Emit)iter.Current).Note.Commands.Single(),
            Is.EqualTo(new SetResonantFilterCommand(0.25, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => iter.MoveNext());
    }

    [TestCase("Filter(0, 0, -0.01, 0.5);")]
    [TestCase("FilterResonance(0, 0, 1.2);")]
    [TestCase("Seek(0, 0, -0.1);")]
    [TestCase("Pan(0, 0, Math.Pow(10, 400));")]
    public void InvalidScriptControlsThrowInsteadOfProducingBadEvents(string source)
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Invalid")
        {
            RowCount = 1,
            ChannelCount = 1,
            Source = source,
        };
        var result = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(result.Success, Is.True);
        using IEnumerator<RawPatternStep> iter =
            result.Program!.EnumerateRawSteps(new SequencingContext())
                .GetEnumerator();
        Assert.Throws<ArgumentOutOfRangeException>(() => iter.MoveNext());
    }

    [Test]
    public void DirectFrequencyIsStillRejectedUntilRendererSemanticsExist()
    {
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(new RawSource(new NoteEvent(
            new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 0),
            ChannelTarget.Physical(0),
            [new SetPlaybackFrequencyCommand(440)])), 1, context);
        Assert.Throws<NotSupportedException>(() => Drain(timeline));
    }

    private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
    {
        List<NoteEvent> emitted = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit e)
                emitted.Add(e.Note);
        return emitted.ToArray();
    }

    private sealed class RawSource(params NoteEvent[] events)
        : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            foreach (NoteEvent note in events)
                yield return new RawPatternStep.Emit(note);
        }
    }
}
