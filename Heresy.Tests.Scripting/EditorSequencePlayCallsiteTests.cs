using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class EditorSequencePlayCallsiteTests
{
    [Test]
    public void OrdinaryAndInstrumentedCompilationHaveIdenticalMusicalEntries()
    {
        const string source = """
            if (sequenceIndex == 0) return Play(_O(17), startRow: 3);
            if (sequenceIndex == 1) return Play(_O(18));
            return null;
            """;
        ScriptSequenceDefinition definition = new((ObjectId)1U, "Instrumented")
            { Source = source };
        var normal = ScriptCompiler.CompileIncrementalSequence(definition);
        var traced = ScriptCompiler.CompileIncrementalSequence(definition,
            instrumentPlayCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(normal.Success, Is.True,
                string.Join("; ", normal.Diagnostics.Select(d => d.Message)));
            Assert.That(traced.Success, Is.True,
                string.Join("; ", traced.Diagnostics.Select(d => d.Message)));
        });
        ISequenceEntryProvider ordinary = normal.Program!.Create(new SequencingContext());
        ISequenceEntryProvider instrumented =
            traced.Program!.Create(new SequencingContext());

        for (int index = 0; index < 3; index++)
        {
            SequenceEntry? a = ordinary.GetSequenceEntry(index, index, index - 1);
            SequenceEntry? b = instrumented.GetSequenceEntry(index, index, index - 1);
            Assert.That(b?.PatternId, Is.EqualTo(a?.PatternId));
            Assert.That(b?.StartRow, Is.EqualTo(a?.StartRow));
            Assert.That(a?.EditorCallsite, Is.Null,
                "regular song playback must not carry tracing data");
            if (index < 2)
            {
                int start = source.IndexOf("Play(", index == 0 ? 0 : source.IndexOf("Play(") + 1,
                    StringComparison.Ordinal);
                Assert.That(b?.EditorCallsite,
                    Is.EqualTo(new SequenceCallsite(start,
                        source.IndexOf(')', start) - start + 1)));
            }
            else
                Assert.That(b?.EditorCallsite, Is.Null);
        }
        Assert.That(definition.Source, Is.EqualTo(source),
            "the instrumentation must never persist changes to authored source");
    }

    [Test]
    public void OwnerReproIdenticalPatternInTwoOrdersHasDistinctPlayIdentifiers()
    {
        const string source = """
            switch (sequenceIndex)
            {
                case 0: return Play(_O(1));
                case 1: return Play(_O(1));
                default: return default;
            }
            """;
        ScriptSequenceDefinition definition = new((ObjectId)2U, "Repro")
            { Source = source };
        var compiled = ScriptCompiler.CompileIncrementalSequence(definition,
            instrumentPlayCallsites: true);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        var provider = compiled.Program!.Create(new SequencingContext());
        SequenceEntry first = provider.GetSequenceEntry(0, 0, -1)!;
        SequenceEntry second = provider.GetSequenceEntry(1, 1, 0)!;
        int firstOffset = source.IndexOf("Play(", StringComparison.Ordinal);
        int secondOffset = source.IndexOf("Play(", firstOffset + 4,
            StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(first.PatternId, Is.EqualTo((ObjectId)1U));
            Assert.That(second.PatternId, Is.EqualTo((ObjectId)1U));
            Assert.That(first.EditorCallsite,
                Is.EqualTo(new SequenceCallsite(firstOffset,
                    source.IndexOf(')', firstOffset) - firstOffset + 1)));
            Assert.That(second.EditorCallsite,
                Is.EqualTo(new SequenceCallsite(secondOffset,
                    source.IndexOf(')', secondOffset) - secondOffset + 1)));
            Assert.That(provider.GetSequenceEntry(2, 2, 1), Is.Null);
        });
    }

    [Test]
    public void ChosenBranchRetainsItsOwnCallsiteDespiteIgnoredPlayAndRepeatedOrders()
    {
        const string source = """
            var ignored = Play(_O(20));
            if (absoluteIndex % 2 == 0)
                return Play(_O(17));
            return Play(_O(18), 1);
            """;
        ScriptSequenceDefinition definition = new((ObjectId)1U, "Branches")
            { Source = source };
        var traced = ScriptCompiler.CompileIncrementalSequence(definition,
            instrumentPlayCallsites: true);
        Assert.That(traced.Success, Is.True,
            string.Join("; ", traced.Diagnostics.Select(d => d.Message)));
        ISequenceEntryProvider provider = traced.Program!.Create(new SequencingContext());
        int firstChosen = source.IndexOf("return Play(", StringComparison.Ordinal) + "return ".Length;
        int lastChosen = source.LastIndexOf("Play(", StringComparison.Ordinal);

        SequenceEntry first = provider.GetSequenceEntry(0, 0, -1)!;
        SequenceEntry second = provider.GetSequenceEntry(1, 0, 0)!;
        SequenceEntry third = provider.GetSequenceEntry(2, 0, 0)!;

        Assert.Multiple(() =>
        {
            Assert.That(first.PatternId, Is.EqualTo((ObjectId)17U));
            Assert.That(second.PatternId, Is.EqualTo((ObjectId)18U));
            Assert.That(third.PatternId, Is.EqualTo((ObjectId)17U));
            Assert.That(first.EditorCallsite,
                Is.EqualTo(new SequenceCallsite(firstChosen,
                    source.IndexOf(')', firstChosen) - firstChosen + 1)));
            Assert.That(second.EditorCallsite,
                Is.EqualTo(new SequenceCallsite(lastChosen,
                    source.IndexOf(')', lastChosen) - lastChosen + 1)));
            Assert.That(third.EditorCallsite, Is.EqualTo(first.EditorCallsite));
        });
    }

    [Test]
    public void PlayWithNestedMultilineArgumentsHighlightsCompleteInvocation()
    {
        const string source = """
            if (sequenceIndex != 0) return default;
            return Play(
                _O(17),
                startRow: 3);
            """;
        ScriptSequenceDefinition def = new((ObjectId)1U, "Multiline")
            { Source = source };
        var compiled = ScriptCompiler.CompileIncrementalSequence(def,
            instrumentPlayCallsites: true);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        SequenceEntry entry = compiled.Program!
            .Create(new SequencingContext()).GetSequenceEntry(0, 0, -1)!;
        int open = source.IndexOf("Play(", StringComparison.Ordinal);
        int close = source.IndexOf(");", open, StringComparison.Ordinal);
        Assert.That(entry.EditorCallsite,
            Is.EqualTo(new SequenceCallsite(open, close - open + 1)));
        Assert.That(source.Substring(entry.EditorCallsite!.Value.Start,
                entry.EditorCallsite!.Value.Length),
            Is.EqualTo(source.Substring(open, close - open + 1)));
        Assert.That(entry.StartRow, Is.EqualTo(3));
    }

    [Test]
    public void LoopRewriterKeepsOffsetsFromOriginalSource()
    {
        const string source = """
            for (int i = 0; i < 2; i++)
            {
                if (i == sequenceIndex) return Play(_O(17));
            }
            return null;
            """;
        var def = new ScriptSequenceDefinition((ObjectId)1U, "Loop")
            { Source = source };
        var compiled = ScriptCompiler.CompileIncrementalSequence(def,
            instrumentPlayCallsites: true);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        var provider = compiled.Program!.Create(new SequencingContext());
        int sourceOffset = source.IndexOf("Play(", StringComparison.Ordinal);
        Assert.That(provider.GetSequenceEntry(0, 0, -1)?.EditorCallsite,
            Is.EqualTo(new SequenceCallsite(sourceOffset,
                source.IndexOf(')', sourceOffset) - sourceOffset + 1)));
        Assert.That(provider.GetSequenceEntry(1, 1, 0)?.EditorCallsite,
            Is.EqualTo(new SequenceCallsite(sourceOffset,
                source.IndexOf(')', sourceOffset) - sourceOffset + 1)));
        Assert.That(provider.GetSequenceEntry(2, 2, 1), Is.Null);
    }

    [Test]
    public void GeneratedOnlyPlayOverloadIsNotAvailableToAuthoredScripts()
    {
        var definition = new ScriptSequenceDefinition((ObjectId)1U, "Restricted")
        {
            Source = "return Play(default(SequenceCallsite), _O(17));",
        };
        var ordinary = ScriptCompiler.CompileIncrementalSequence(definition);
        var instrumented = ScriptCompiler.CompileIncrementalSequence(definition,
            instrumentPlayCallsites: true);
        Assert.That(ordinary.Success, Is.False);
        Assert.That(instrumented.Success, Is.False);
        Assert.That(ordinary.Diagnostics, Has.Some.Property("Code").EqualTo("HRS2001"));
        Assert.That(instrumented.Diagnostics, Has.Some.Property("Code").EqualTo("HRS2001"));

        definition.Source = "return System.IO.File.ReadAllText(\"secret\");";
        instrumented = ScriptCompiler.CompileIncrementalSequence(definition,
            instrumentPlayCallsites: true);
        Assert.That(instrumented.Success, Is.False);
    }

    [Test]
    public void RecursiveTimelineEmitsTraceOnlyForSelectedRootSequenceOrders()
    {
        SongDocument song = new();
        DataPatternDefinition pattern = new((ObjectId)17U, "Notes")
        {
            RowCount = 2,
            ChannelCount = 1,
        };
        ScriptSequenceDefinition sequence = new((ObjectId)4U, "Song")
        {
            Source = """
                if (sequenceIndex == 0) return Play(_O(17));
                if (sequenceIndex == 1) return Play(_O(17), 1);
                return null;
                """,
        };
        song.Add(pattern);
        song.Add(sequence);
        SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(song);

        static List<(int? Order, int Row, SequenceCallsite? Span)> Walk(
            SongDocumentSnapshot snapshot, ObjectId id, bool traced)
        {
            var prepared = new PreparedRoslynIncrementalScriptSources(snapshot,
                traced ? id : null);
            using IncrementalRecursiveTimeline timeline =
                prepared.CreateTimeline(new SequencingContext());
            List<(int?, int, SequenceCallsite?)> calls = [];
            timeline.EditorRowBegan += (_, row, sequenceId, order, _, callsite) =>
            {
                Assert.That(sequenceId, Is.EqualTo(id));
                calls.Add((order, row, callsite));
            };
            timeline.AddRoot(id);
            int guard = 0;
            while (timeline.TryStep(out _) && ++guard < 4096) { }
            Assert.That(guard, Is.LessThan(4096));
            return calls;
        }
        var active = Walk(snapshot, sequence.Id, traced: true);
        var normal = Walk(snapshot, sequence.Id, traced: false);

        Assert.That(active, Has.Count.EqualTo(3));
        Assert.That(normal, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(active.Select(x => x.Order), Is.EqualTo(new int?[] { 0, 0, 1 }));
            Assert.That(active.Select(x => x.Row), Is.EqualTo(new[] { 0, 1, 1 }));
            Assert.That(active[0].Span, Is.EqualTo(active[1].Span));
            Assert.That(active[0].Span, Is.Not.EqualTo(active[2].Span));
            Assert.That(normal.All(x => x.Span is null), Is.True);
        });
    }
}
