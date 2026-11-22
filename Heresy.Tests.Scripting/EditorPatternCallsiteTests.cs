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
                Assert.That(a[i].EditorPatternRow, Is.Null);
                Assert.That(b[i].EditorPatternRow, Is.EqualTo(
                    i switch { 0 => 0, 1 => 1, 2 or 3 => 2, _ => 3 }));
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
    public void EditorF6TracesVirtualControlsWithoutChangingTypedCommands()
    {
        const string source = """
            VolumeVirtual(0, 7U, .25);
            SourceFrameVirtual(
                1, 7U, 256,
                timeOffsetSeconds: .01);
            DisplacementVirtual(2, 7U, NoteDisplacementAction.Continue);
            PastVirtual(3, 7U, TrackerPastNoteAction.Cut);
            """;
        string[] helperNames =
        {
            "VolumeVirtual(", "SourceFrameVirtual(",
            "DisplacementVirtual(", "PastVirtual(",
        };
        ScriptPatternDefinition pattern = new((ObjectId)12U, "Virtual controls")
        {
            Source = source, RowCount = 8, ChannelCount = 1,
        };
        var ordinaryCompile = ScriptCompiler.CompileIncrementalPattern(pattern);
        var tracedCompile = ScriptCompiler.CompileIncrementalPattern(pattern,
            instrumentActionCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(ordinaryCompile.Success, Is.True,
                string.Join("; ", ordinaryCompile.Diagnostics.Select(d => d.Message)));
            Assert.That(tracedCompile.Success, Is.True,
                string.Join("; ", tracedCompile.Diagnostics.Select(d => d.Message)));
        });
        NoteEvent[] ordinary = ordinaryCompile.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        NoteEvent[] traced = tracedCompile.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        Assert.That(ordinary, Has.Length.EqualTo(helperNames.Length));
        Assert.That(traced, Has.Length.EqualTo(helperNames.Length));
        for (int i = 0; i < helperNames.Length; i++)
        {
            int start = source.IndexOf(helperNames[i], StringComparison.Ordinal);
            int end = i + 1 < helperNames.Length
                ? source.LastIndexOf(");", source.IndexOf(helperNames[i + 1],
                    StringComparison.Ordinal) - 1, StringComparison.Ordinal) + 1
                : source.LastIndexOf(");", StringComparison.Ordinal) + 1;
            Assert.Multiple(() =>
            {
                Assert.That(ordinary[i].EditorCallsite, Is.Null);
                Assert.That(ordinary[i].EditorPatternRow, Is.Null);
                Assert.That(traced[i].EditorCallsite,
                    Is.EqualTo(new SequenceCallsite(start, end - start)));
                Assert.That(traced[i].EditorPatternRow, Is.EqualTo(i));
                Assert.That(traced[i].Offset, Is.EqualTo(ordinary[i].Offset));
                Assert.That(traced[i].Target, Is.EqualTo(ordinary[i].Target));
                Assert.That(traced[i].Commands, Is.EqualTo(ordinary[i].Commands));
            });
        }
        Assert.That(pattern.Source, Is.EqualTo(source));
    }

    [TestCase("VolumeVirtual(default(SequenceCallsite), 0, 7U, .25);")]
    [TestCase("SourceFrameVirtual(default(SequenceCallsite), 0, 7U, 256);")]
    [TestCase("DisplacementVirtual(default(SequenceCallsite), 0, 7U, NoteDisplacementAction.Cut);")]
    [TestCase("PastVirtual(default(SequenceCallsite), 0, 7U, TrackerPastNoteAction.Cut);")]
    public void AuthoredSourceCannotSpoofVirtualControlCallsites(string source)
    {
        ScriptPatternDefinition pattern = new((ObjectId)13U, "Restricted")
        {
            RowCount = 4, ChannelCount = 1, Source = source,
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
    public void EditorF6TracesTimingAndPhysicalControlsWithoutChangingMusic()
    {
        // A multiline argument list checks that original Roslyn spans
        // include the full authored expression, not just the identifier.
        const string source = """
            Tempo(0, 125);
            Speed(1, 6);
            Seek(2, 0, 0.25);
            Pan(
                3, 0,
                x: 0.4,
                y: -0.2);
            Surround(4, 0, true);
            Filter(5, 0, .4, .3);
            FilterCutoff(6, 0, .7);
            FilterResonance(7, 0, .6);
            """;
        string[] helpers = { "Tempo(", "Speed(", "Seek(", "Pan(",
            "Surround(", "Filter(", "FilterCutoff(", "FilterResonance(" };
        ScriptPatternDefinition pattern = new((ObjectId)11U, "Controls")
        {
            Source = source, RowCount = 12, ChannelCount = 1,
        };
        var ordinaryCompile = ScriptCompiler.CompileIncrementalPattern(pattern);
        var tracedCompile = ScriptCompiler.CompileIncrementalPattern(pattern,
            instrumentActionCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(ordinaryCompile.Success, Is.True,
                string.Join("; ", ordinaryCompile.Diagnostics.Select(d => d.Message)));
            Assert.That(tracedCompile.Success, Is.True,
                string.Join("; ", tracedCompile.Diagnostics.Select(d => d.Message)));
        });
        NoteEvent[] ordinary = ordinaryCompile.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        NoteEvent[] traced = tracedCompile.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        Assert.That(ordinary, Has.Length.EqualTo(helpers.Length));
        Assert.That(traced, Has.Length.EqualTo(helpers.Length));
        for (int i = 0; i < helpers.Length; i++)
        {
            int start = source.IndexOf(helpers[i], StringComparison.Ordinal);
            int end = i + 1 < helpers.Length
                ? source.LastIndexOf(");", source.IndexOf(helpers[i + 1],
                    StringComparison.Ordinal) - 1, StringComparison.Ordinal) + 1
                : source.LastIndexOf(");", StringComparison.Ordinal) + 1;
            Assert.Multiple(() =>
            {
                Assert.That(ordinary[i].EditorCallsite, Is.Null);
                Assert.That(ordinary[i].EditorPatternRow, Is.Null);
                Assert.That(traced[i].EditorCallsite,
                    Is.EqualTo(new SequenceCallsite(start, end - start)));
                Assert.That(traced[i].EditorPatternRow, Is.EqualTo(i));
                Assert.That(ordinary[i].Offset, Is.EqualTo(traced[i].Offset));
                Assert.That(ordinary[i].Target, Is.EqualTo(traced[i].Target));
                Assert.That(ordinary[i].Commands.Select(c => c.GetType()),
                    Is.EqualTo(traced[i].Commands.Select(c => c.GetType())));
                Assert.That(ordinary[i].Commands.Select(c => c.ToString()),
                    Is.EqualTo(traced[i].Commands.Select(c => c.ToString())));
            });
        }
        Assert.That(pattern.Source, Is.EqualTo(source));
    }

    [TestCase("Tempo(default(SequenceCallsite), 0, 125);")]
    [TestCase("Speed(default(SequenceCallsite), 0, 6);")]
    [TestCase("Pan(default(SequenceCallsite), 0, 0, .2);")]
    [TestCase("FilterResonance(default(SequenceCallsite), 0, 0, .5);")]
    public void AuthoredSourceCannotSpoofControlCallsites(string source)
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Restricted")
        {
            RowCount = 4, ChannelCount = 1, Source = source,
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
    public void EditorF6TracesAllVoiceEndingHelpersWithoutChangingCommands()
    {
        const string source = """
            Off(0, 0);
            Cut(1, 0);
            OffVirtual(2, 7);
            CutVirtual(3, 7);
            OffVirtualInScope(4);
            CutVirtualInScope(5);
            OffAllVirtual(6);
            CutAllVirtual(7);
            """;
        string[] names = { "Off(", "Cut(", "OffVirtual(", "CutVirtual(",
            "OffVirtualInScope(", "CutVirtualInScope(", "OffAllVirtual(",
            "CutAllVirtual(" };
        ScriptPatternDefinition pattern = new((ObjectId)7U, "Voice endings")
        {
            RowCount = 10, ChannelCount = 1, Source = source,
        };
        var normal = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(pattern,
            instrumentActionCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(normal.Success, Is.True,
                string.Join("; ", normal.Diagnostics.Select(d => d.Message)));
            Assert.That(traced.Success, Is.True,
                string.Join("; ", traced.Diagnostics.Select(d => d.Message)));
        });
        NoteEvent[] ordinary = normal.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        NoteEvent[] annotated = traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Note).ToArray();
        Assert.That(annotated.Length, Is.EqualTo(names.Length));
        for (int i = 0; i < annotated.Length; i++)
        {
            int first = source.IndexOf(names[i], StringComparison.Ordinal);
            int end = source.IndexOf(')', first) + 1;
            Assert.Multiple(() =>
            {
                Assert.That(ordinary[i].EditorCallsite, Is.Null);
                Assert.That(annotated[i].EditorCallsite,
                    Is.EqualTo(new SequenceCallsite(first, end - first)));
                Assert.That(annotated[i].EditorPatternRow, Is.EqualTo(i));
                Assert.That(ordinary[i].Target, Is.EqualTo(annotated[i].Target));
                Assert.That(ordinary[i].Offset, Is.EqualTo(annotated[i].Offset));
                Assert.That(ordinary[i].Commands.Select(x => x.GetType()),
                    Is.EqualTo(annotated[i].Commands.Select(x => x.GetType())));
            });
        }
        Assert.That(pattern.Source, Is.EqualTo(source));
    }

    [TestCase("Off(default(SequenceCallsite), 0, 0);")]
    [TestCase("CutAllVirtual(default(SequenceCallsite), 0);")]
    public void AuthoredSourceCannotSpoofVoiceEndingCallsites(string source)
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Restricted")
        {
            RowCount = 4, ChannelCount = 1, Source = source,
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
