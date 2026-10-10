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
public sealed class ScriptVirtualChannelTests
{
    private const string LifecycleSource = """
        NoteVirtual(0, 123U, _O(17), 2.0, 0.5, true, 0.75);
        NoteVirtual(0.25, 123U, _O(18), timeOffsetSeconds: 0.01);
        OffVirtual(0.5, 123U);
        CutVirtual(0.75, 123U);
        OffVirtualInScope(1);
        CutVirtualInScope(1.25);
        OffAllVirtual(1.5);
        CutAllVirtual(2);
        """;

    [Test]
    public void EagerAndIncrementalCompilersProduceIdenticalVirtualTargetsAndCommands()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Virtual notes")
        {
            RowCount = 2, ChannelCount = 1,
            Source = LifecycleSource,
        };
        var eager = ScriptCompiler.CompilePattern(definition);
        var incremental = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.Multiple(() =>
        {
            Assert.That(eager.Success, Is.True,
                string.Join("; ", eager.Diagnostics.Select(x => x.Message)));
            Assert.That(incremental.Success, Is.True,
                string.Join("; ", incremental.Diagnostics.Select(x => x.Message)));
        });
        NoteScheduleBuilder output = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), output, out _);
        NoteEvent[] eagerEvents = output.Freeze().ToArray();
        NoteEvent[] incrementalEvents = incremental.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>()
            .Select(x => x.Note).ToArray();
        Assert.That(eagerEvents, Has.Length.EqualTo(8));
        Assert.That(incrementalEvents, Has.Length.EqualTo(8));
        ChannelTarget[] targets =
        [
            ChannelTarget.Virtual(123U), ChannelTarget.Virtual(123U),
            ChannelTarget.Virtual(123U), ChannelTarget.Virtual(123U),
            ChannelTarget.AllVirtualInScope, ChannelTarget.AllVirtualInScope,
            ChannelTarget.AllVirtual, ChannelTarget.AllVirtual,
        ];
        Assert.Multiple(() =>
        {
            Assert.That(eagerEvents.Select(x => x.Target), Is.EqualTo(targets));
            Assert.That(incrementalEvents.Select(x => x.Target), Is.EqualTo(targets));
            Assert.That(incrementalEvents.Select(x => x.Offset),
                Is.EqualTo(eagerEvents.Select(x => x.Offset)));
            Assert.That(incrementalEvents.Select(x => x.Commands.Single()),
                Is.EqualTo(eagerEvents.Select(x => x.Commands.Single())));
            Assert.That(incrementalEvents[0].Commands.Single(),
                Is.EqualTo(new StartNoteCommand((ObjectId)17U, 2.0, 0.5, true, 0.75)));
            Assert.That(incrementalEvents[1].Offset.TimeOffset,
                Is.EqualTo(TimeSpan.FromMilliseconds(10)));
            Assert.That(incrementalEvents.Skip(2).Select(x => x.Commands.Single()),
                Is.EqualTo(new NoteCommand[] {
                    new NoteOffCommand(), new NoteCutCommand(),
                    new NoteOffCommand(), new NoteCutCommand(),
                    new NoteOffCommand(), new NoteCutCommand(),
                }));
        });
    }

    [Test]
    public void StreamingVirtualHelpersSuspendAfterEveryEvent()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Lazy")
        {
            RowCount = 2, ChannelCount = 1,
            Source = """
                NoteVirtual(0, 4U, _O(17));
                OffVirtual(0.25, 4U);
                CutVirtualInScope(0.5);
                OffAllVirtual(1);
                """,
        };
        var compilation = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compilation.Success, Is.True,
            string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        using IEnumerator<RawPatternStep> iterator =
            compilation.Program!.EnumerateRawSteps(new SequencingContext())
                .GetEnumerator();
        foreach (ChannelTarget target in new[] {
            ChannelTarget.Virtual(4U), ChannelTarget.Virtual(4U),
            ChannelTarget.AllVirtualInScope, ChannelTarget.AllVirtual,
        })
        {
            Assert.That(iterator.MoveNext(), Is.True);
            Assert.That(iterator.Current, Is.TypeOf<RawPatternStep.Emit>());
            Assert.That(((RawPatternStep.Emit)iterator.Current).Note.Target,
                Is.EqualTo(target));
        }
        Assert.That(iterator.MoveNext(), Is.False);
    }

    [Test]
    public void VirtualNotesRetainLocalIdAndFixedWallTimeUnderSharedTempoChanges()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Virtual deadline")
        {
            RowCount = 2, ChannelCount = 1,
            Source = """
                Tempo(0, 250);
                NoteVirtual(0.5, 0U, _O(17), timeOffsetSeconds: 0.025);
                CutVirtual(1, 0U);
                """,
        };
        var compilation = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compilation.Success, Is.True,
            string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(compilation.Program!, definition.RowCount, context);
        List<NoteEvent> emitted = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit e)
                emitted.Add(e.Note);
        Assert.Multiple(() =>
        {
            Assert.That(emitted.Select(x => x.Offset.TimeOffset.TotalSeconds),
                Is.EqualTo(new[] { 0.0, 0.055, 0.06 }).Within(1e-6));
            Assert.That(emitted.Select(x => x.Target), Is.EqualTo(new[] {
                ChannelTarget.Global, ChannelTarget.Virtual(0U),
                ChannelTarget.Virtual(0U),
            }));
        });
    }

    [TestCase("NoteVirtual(0, 7U, _O(17), pitchMultiplier: 0);")]
    [TestCase("NoteVirtual(0, 7U, _O(17), volume: 1.1);")]
    [TestCase("NoteVirtual(0, 7U, _O(0));")]
    [TestCase("CutVirtual(1.5, 7U);")]
    [TestCase("OffVirtualInScope(0, timeOffsetSeconds: 1e300);")]
    [TestCase("CutAllVirtual(0, timeOffsetSeconds: 1e300);")]
    public void InvalidVirtualHelperFailsBeforeEmitting(string source)
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Invalid virtual")
        {
            RowCount = 1, ChannelCount = 1, Source = source,
        };
        var compilation = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compilation.Success, Is.True);
        using IEnumerator<RawPatternStep> iterator =
            compilation.Program!.EnumerateRawSteps(new SequencingContext())
                .GetEnumerator();
        Assert.Throws<ArgumentOutOfRangeException>(() => iterator.MoveNext());
    }

    [Test]
    public void VirtualEmissionInsideAnExpressionIsRejectedByIncrementalCompiler()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Not a statement")
        {
            RowCount = 2, ChannelCount = 1,
            Source = "for (int i = 0; i < 1; OffVirtual(i, 7U)) { }",
        };
        var compilation = ScriptCompiler.CompileIncrementalPattern(definition);
        Assert.That(compilation.Success, Is.False);
        Assert.That(compilation.Diagnostics.Any(d => d.Code == "HRS2001"
            && d.Message.Contains("direct statements", StringComparison.Ordinal)),
            Is.True);
    }
}
