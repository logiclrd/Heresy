using System;
using System.Linq;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class EditorPatternCallsiteTests
{
    [Test]
    public void IncrementalPatternTracesFullAuthoredActionsWithoutChangingMusic()
    {
        const string source = """
            Note(0, 0, _O(17), Semitone(2));
            NoteVirtual(
                1, 7, _O(17),
                timeOffsetSeconds: 0.02);
            NoteFilter(2, 0, _O(30));
            NoteFilterVirtual(2, 7);
            ChannelFilter(3, 0);
            ChannelFilterVirtual(3, 7);
            """;
        ScriptPatternDefinition pattern = new((ObjectId)2U, "Actions")
        {
            Source = source, RowCount = 8, ChannelCount = 1,
        };
        var ordinary = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(pattern,
            instrumentActionCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(ordinary.Success, Is.True,
                string.Join("; ", ordinary.Diagnostics.Select(d => d.Message)));
            Assert.That(traced.Success, Is.True,
                string.Join("; ", traced.Diagnostics.Select(d => d.Message)));
        });
        NoteEvent[] a = ordinary.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();
        NoteEvent[] b = traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();

        Assert.That(b, Has.Length.EqualTo(6));
        Assert.That(a, Has.Length.EqualTo(b.Length));
        string[] helpers = { "Note(", "NoteVirtual(", "NoteFilter(",
            "NoteFilterVirtual(", "ChannelFilter(", "ChannelFilterVirtual(" };
        for (int i = 0; i < b.Length; i++)
        {
            int start = source.IndexOf(helpers[i], StringComparison.Ordinal);
            int next = source.IndexOf(");", start, StringComparison.Ordinal) + 1;
            Assert.Multiple(() =>
            {
                Assert.That(a[i].EditorCallsite, Is.Null);
                Assert.That(b[i].EditorCallsite,
                    Is.EqualTo(new SequenceCallsite(start, next - start)));
                Assert.That(a[i].Offset, Is.EqualTo(b[i].Offset));
                Assert.That(a[i].Target, Is.EqualTo(b[i].Target));
                Assert.That(a[i].Commands.Select(c => c.GetType()),
                    Is.EqualTo(b[i].Commands.Select(c => c.GetType())));
            });
        }
        Assert.That(pattern.Source, Is.EqualTo(source),
            "trusted instrumentation cannot change authored song source");
    }

    [Test]
    public void AuthorCannotCallGeneratedOnlyPatternOverloads()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Restricted")
        {
            RowCount = 4, ChannelCount = 1,
            Source = "Note(default(SequenceCallsite), 0, 0, _O(17));",
        };
        Assert.That(ScriptCompiler.CompileIncrementalPattern(pattern).Success,
            Is.False);
        var traced = ScriptCompiler.CompileIncrementalPattern(pattern,
            instrumentActionCallsites: true);
        Assert.That(traced.Success, Is.False);
        Assert.That(traced.Diagnostics,
            Has.Some.Property("Code").EqualTo("HRS2001"));
    }

    [Test]
    public void RootTimelineReportsDuePatternActionsOnlyWhenOptedIn()
    {
        const string source = """
            Note(0, 0, _O(17));
            Note(1, 0, _O(17), timeOffsetSeconds: 0.01);
            """;
        SongDocument song = new();
        ScriptPatternDefinition pattern = new((ObjectId)5U, "Timeline")
        {
            Source = source, RowCount = 4, ChannelCount = 1,
        };
        song.Add(pattern);
        SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(song);

        static List<(int Row, TimeSpan At, SequenceCallsite Span)> Run(
            SongDocumentSnapshot snapshot, ObjectId id, bool traced)
        {
            PreparedRoslynIncrementalScriptSources prepared = new(snapshot,
                editorPatternCallsites: traced ? id : null);
            using IncrementalRecursiveTimeline timeline =
                prepared.CreateTimeline(new SequencingContext());
            List<(int, TimeSpan, SequenceCallsite)> actual = [];
            timeline.EditorPatternActionBegan += (patternId, row, at, span) =>
            {
                Assert.That(patternId, Is.EqualTo(id));
                actual.Add((row, at, span));
            };
            timeline.AddRoot(id);
            int guard = 0;
            while (timeline.TryStep(out _) && ++guard < 4096) { }
            Assert.That(guard, Is.LessThan(4096));
            return actual;
        }

        var normal = Run(snapshot, pattern.Id, traced: false);
        var actions = Run(snapshot, pattern.Id, traced: true);
        Assert.That(normal, Is.Empty);
        Assert.That(actions, Has.Count.EqualTo(2));
        Assert.That(actions.Select(x => x.Row), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(actions[0].At, Is.LessThan(actions[1].At));
        int first = source.IndexOf("Note(", StringComparison.Ordinal);
        int second = source.IndexOf("Note(", first + 1, StringComparison.Ordinal);
        Assert.That(actions[0].Span,
            Is.EqualTo(new SequenceCallsite(first, "Note(0, 0, _O(17))".Length)));
        Assert.That(actions[1].Span,
            Is.EqualTo(new SequenceCallsite(second,
                "Note(1, 0, _O(17), timeOffsetSeconds: 0.01)".Length)));
    }
}
