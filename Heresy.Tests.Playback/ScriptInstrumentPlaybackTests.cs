using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Sequences;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class ScriptInstrumentPlaybackTests
{
    [Test]
    public void PreparedScriptedInstrumentNnaFollowsIncomingPitchAndPreservesBoundVoicePolicy()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "NNA by pitch")
        {
            Source = "0",
            NnaExpression = "pitchMultiplier < 1.5 ? InstrumentNnaAction.Continue : InstrumentNnaAction.Cut",
        };
        instrument.ToneSpecifications.Add(new ToneSpecification
        { SourceId = sample });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({id.Value}), pitchMultiplier: 1.0); " +
            $"Note(1, 0, _O({id.Value}), pitchMultiplier: 2.0);");

        using PreparedIncrementalPlaybackPlan stable =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        // Source preparation freezes the first note's compiled NNA; changing
        // the mutable song after plan creation cannot cut a pre-bound voice.
        instrument.NnaExpression = "InstrumentNnaAction.Cut";
        using PreparedIncrementalPlaybackPlan cut =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] withContinue = new float[160], withCut = new float[160];
        stable.Source.Render(withContinue.Length, withContinue);
        cut.Source.Render(withCut.Length, withCut);
        Assert.That(withContinue[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(withContinue[125], Is.GreaterThan(withCut[125] * 1.8f),
            "the first note's Continue policy survives the second note's "
            + "Cut selection and stays independent of later authoring edits");
    }

    [TestCase(1.0, 0.5)]
    [TestCase(2.0, 0.25)]
    public void ScriptSelectsDifferentSourceForEachNotePitch(double pitch, double expected)
    {
        SongDocument song = new();
        ObjectId loud = Sample(song, 16384);
        ObjectId quiet = Sample(song, 8192);
        ObjectId inst = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(inst, "Key split")
        { Source = "pitchMultiplier < 1.5 ? 0 : 1" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = loud });
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = quiet });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({inst.Value}), pitchMultiplier: {pitch.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[10];
        plan.Source.Render(10, pcm);
        Assert.That(pcm, Is.All.EqualTo((float)expected).Within(1e-5f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScriptToneFilterProcessesDirectAndPrivatePatternAudio(bool privatePattern)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId source = privatePattern
            ? ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));")
            : sample;
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition scripted = new(id, "Filtered script tone")
        { Source = "0", PitchExpression = "pitchMultiplier" };
        scripted.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = source,
            FilterConfiguration = filter,
        });
        song.Add(scripted);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({id.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan chunked = factory.Create(song, root);
        float[] expected = new float[12];
        float[] actual = new float[12];
        whole.Source.Render(12, expected);
        chunked.Source.Render(1, actual.AsSpan(0, 1));
        chunked.Source.Render(2, actual.AsSpan(1, 2));
        chunked.Source.Render(9, actual.AsSpan(3));
        Assert.That(expected[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(expected[1], Is.EqualTo(1.0f).Within(1e-5f),
            "the tone Filter must survive compiled script pitch selection");
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedDataToneFiltersComposeThroughPrivatePatternOrSequence(
        bool viaSequence)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId pattern = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}));");
        ObjectId terminal = pattern;
        if (viaSequence)
        {
            ObjectId sequenceId = song.AllocateObjectId();
            DataSequenceDefinition sequence = new(sequenceId, "Private sequence");
            sequence.Entries.Add(new SequenceEntry(pattern));
            song.Add(sequence);
            terminal = sequenceId;
        }
        ObjectId innerId = song.AllocateObjectId();
        InstrumentDefinition inner = new(innerId, "Inner");
        inner.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = terminal, FilterConfiguration = Echo(song, 0.001),
        });
        inner.ToneTable.Add(0);
        song.Add(inner);
        ObjectId outerId = song.AllocateObjectId();
        InstrumentDefinition outer = new(outerId, "Outer");
        outer.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = innerId, FilterConfiguration = Echo(song, 0.002),
        });
        outer.ToneTable.Add(0);
        song.Add(outer);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({outerId.Value}));");

        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan unsplit = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan partitioned = factory.Create(song, root);
        float[] actual = new float[12], reference = new float[12];
        unsplit.Source.Render(12, actual);
        for (int pos = 0; pos < reference.Length;)
        {
            int count = Math.Min(2, reference.Length - pos);
            partitioned.Source.Render(count, reference.AsSpan(pos, count));
            pos += count;
        }
        Assert.That(actual.Take(4),
            Is.EqualTo(new float[] { 0.5f, 1.0f, 1.5f, 2.0f })
                .Within(1e-5f),
            "two nested tone Filters apply to the private source PCM, inner to outer");
        Assert.That(reference, Is.EqualTo(actual).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScriptNoteAndVirtualNoteSelectEventFilterWithoutSiblingLeak(
        bool useVirtual)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance selected = Echo(song, 0.001);
        ObjectId id = ScriptPattern(song,
            useVirtual
                ? $"NoteVirtual(0, 7U, _O({sample.Value}), " +
                  $"eventFilterDefinitionId: _O({selected.DefinitionId.Value})); " +
                  $"Note(0, 0, _O({sample.Value}));"
                : $"Note(0, 0, _O({sample.Value}), " +
                  $"eventFilterDefinitionId: _O({selected.DefinitionId.Value})); " +
                  $"Note(0, 1, _O({sample.Value}));");
        if (!useVirtual)
            ((ScriptPatternDefinition)song.Objects[id]).ChannelCount = 2;
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan first = factory.Create(song, id);
        using PreparedIncrementalPlaybackPlan second = factory.Create(song, id);
        float[] whole = new float[8], partitioned = new float[8];
        first.Source.Render(whole.Length, whole);
        second.Source.Render(1, partitioned.AsSpan(0, 1));
        second.Source.Render(7, partitioned.AsSpan(1));
        Assert.That(whole[0], Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(whole[1], Is.EqualTo(1.5f).Within(1e-5f),
            "only the selected note acquires its extra delayed sample");
        Assert.That(partitioned, Is.EqualTo(whole).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelectedNoteEventFilterIsBoundAcrossDirectAndPrivatePatternSources(
        bool privatePattern)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId source = privatePattern
            ? ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));")
            : sample;
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = ScriptPattern(song,
            $"Note(0, 0, _O({source.Value}), mixdown: {privatePattern.ToString().ToLowerInvariant()}, " +
            $"eventFilterDefinitionId: _O({filter.DefinitionId.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(song, id);
        float[] pcm = new float[12];
        plan.Source.Render(pcm.Length, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.0f).Within(1e-5f),
            "the outer event filter is applied to either terminal source");
    }

    [Test]
    public void DataPatternEventFilterIdPersistsAndResolvesDuringProductionPlayback()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = song.AllocateObjectId();
        DataPatternDefinition pattern = new(id, "Data event")
        { RowCount = 3, ChannelCount = 1 };
        pattern.Grid.GetOrCreateCell(0, 0).Note =
            new StartPatternNote(sample,
                eventFilterDefinitionId: filter.DefinitionId);
        song.Add(pattern);
        string json = Heresy.Core.Persistence.SongDocumentJson.SerializePatternNote(
            pattern.Grid[0, 0]!.Note!);
        StartPatternNote roundtrip =
            (StartPatternNote)Heresy.Core.Persistence.SongDocumentJson.DeserializePatternNote(json);
        Assert.That(roundtrip.EventFilterDefinitionId,
            Is.EqualTo(filter.DefinitionId));
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, id);
        float[] pcm = new float[8];
        plan.Source.Render(pcm.Length, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.0f).Within(1e-5f));
    }

    [Test]
    public void UnknownScriptNoteFilterFailsExplicitlyRatherThanSilentlyBypassing()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}), eventFilterDefinitionId: _O(987654));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, id);
        Assert.Throws<InvalidOperationException>(() =>
            plan.Source.Render(4, new float[4]));
    }

    [Test]
    public void FlattenedNoteStartFilterIsInheritedByDescendantNotUnrelatedSibling()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId child = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}));");
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({child.Value}), " +
            $"eventFilterDefinitionId: _O({filter.DefinitionId.Value})); " +
            $"Note(0, 1, _O({sample.Value}));");
        ((ScriptPatternDefinition)song.Objects[root]).ChannelCount = 2;
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan parts = factory.Create(song, root);
        float[] pcm = new float[8], chunked = new float[8];
        whole.Source.Render(pcm.Length, pcm);
        parts.Source.Render(1, chunked.AsSpan(0, 1));
        parts.Source.Render(7, chunked.AsSpan(1));
        Assert.That(pcm[0], Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.5f).Within(1e-5f),
            "the flattened child uses the event Filter, but its sibling does not");
        Assert.That(chunked, Is.EqualTo(pcm).Within(1e-6f));
    }

    [Test]
    public void NestedFlattenedInstigatingNotesComposeTheirEventFilterLayers()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance inside = Echo(song, 0.001);
        FilterInstance outside = Echo(song, 0.002);
        ObjectId leaf = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}));");
        ObjectId middle = ScriptPattern(song,
            $"Note(0, 0, _O({leaf.Value}), " +
            $"eventFilterDefinitionId: _O({inside.DefinitionId.Value}));");
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({middle.Value}), " +
            $"eventFilterDefinitionId: _O({outside.DefinitionId.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan chunks = factory.Create(song, root);
        float[] pcm = new float[8], split = new float[8];
        whole.Source.Render(8, pcm);
        for (int i = 0; i < split.Length; i++)
            chunks.Source.Render(1, split.AsSpan(i, 1));
        Assert.That(pcm.Take(4),
            Is.EqualTo(new float[] { 0.5f, 1.0f, 1.5f, 2.0f })
                .Within(1e-5f));
        Assert.That(split, Is.EqualTo(pcm).Within(1e-6f));
    }

    [Test]
    public void ScriptNoteFilterSelectAndClearAreChronological()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            Note(0, 0, _O({sample.Value}));
            NoteFilter(0, 0, _O({filter.DefinitionId.Value}));
            NoteFilter(0, 0, timeOffsetSeconds: 0.003);
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan full = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan split = factory.Create(song, root);
        float[] pcm = new float[8], chunks = new float[8];
        full.Source.Render(8, pcm);
        split.Source.Render(1, chunks.AsSpan(0, 1));
        split.Source.Render(2, chunks.AsSpan(1, 2));
        split.Source.Render(5, chunks.AsSpan(3));
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.0f).Within(1e-5f),
            "the selected Filter adds one delayed half-amplitude sample");
        Assert.That(pcm[2], Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(pcm[3], Is.EqualTo(0.5f).Within(1e-5f),
            "clearing the event Filter affects only the subsequent PCM");
        Assert.That(chunks, Is.EqualTo(pcm).Within(1e-6f));
    }

    [Test]
    public void ScriptVirtualNoteFilterSelectDoesNotTouchPhysicalSibling()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            Note(0, 0, _O({sample.Value}));
            NoteVirtual(0, 7U, _O({sample.Value}));
            NoteFilterVirtual(0, 7U, _O({filter.DefinitionId.Value}));
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(song, root);
        float[] pcm = new float[5];
        plan.Source.Render(5, pcm);
        Assert.That(pcm[0], Is.EqualTo(1f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.5f).Within(1e-5f),
            "only virtual note gains the additional delayed sample");
    }

    [Test]
    public void ScriptNoNoteAndMissingFilterSelectionsProduceRecoverableDiagnostics()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance defined = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            NoteFilter(0, 0, _O({defined.DefinitionId.Value}));
            Note(0, 0, _O({sample.Value}));
            NoteFilter(0, 0, _O(998877));
            """);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[5];
        plan.Source.Render(5, pcm);
        Assert.That(pcm, Is.All.EqualTo(0.5f).Within(1e-5f),
            "invalid/no-note updates must never alter the unfiltered voice");
        var diagnostics = plan.SequencingContext.Diagnostics.Drain();
        Assert.That(diagnostics, Has.Length.EqualTo(2));
        Assert.That(diagnostics.All(d => d.Code == "HRSEQ007"), Is.True);
        Assert.That(diagnostics[0].Message, Does.Contain("no current note"));
        Assert.That(diagnostics[1].Message, Does.Contain("unavailable"));
    }

    [Test]
    public void ScriptChannelFilterSelectionPersistsAcrossBlocksThenClears()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance selected = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            ChannelFilter(0, 0, _O({selected.DefinitionId.Value}));
            Note(0, 0, _O({sample.Value}));
            // Source continues after its note start: channel filtering is
            // persistent and the control is deferred to its wall deadline.
            ChannelFilter(0, 0, timeOffsetSeconds: 0.004);
            """);
        using PreparedIncrementalPlaybackPlan full =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        using PreparedIncrementalPlaybackPlan blocks =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] a = new float[96], b = new float[96];
        full.Source.Render(96, a);
        blocks.Source.Render(1, b.AsSpan(0, 1));
        blocks.Source.Render(95, b.AsSpan(1));
        Assert.That(a, Is.EqualTo(b).Within(1e-6f));
        Assert.That(a[0], Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(a[1], Is.EqualTo(1f).Within(1e-6f));
        Assert.That(a[3], Is.EqualTo(1f).Within(1e-6f));
        Assert.That(a[4], Is.EqualTo(0.5f).Within(1e-6f),
            "the channel Filter clears at its fixed-wall deadline");
    }

    [Test]
    public void ScriptInvalidChannelSelectionDiagnosesWithoutCancelingCurrentHostFilter()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance selected = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            ChannelFilter(0, 0, _O({selected.DefinitionId.Value}));
            ChannelFilter(0, 0, _O(987654));
            Note(0, 0, _O({sample.Value}));
            """);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[8];
        plan.Source.Render(8, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(pcm[1], Is.EqualTo(1f).Within(1e-6f));
        var messages = plan.SequencingContext.Diagnostics.Drain();
        Assert.That(messages.Count(m => m.Code == "HRSEQ009"), Is.EqualTo(1));
    }

    [Test]
    public void ScriptVirtualChannelFilterSelectionIsIndependentOfPhysicalOutput()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = ScriptPattern(song, $"""
            ChannelFilterVirtual(0, 7U, _O({filter.DefinitionId.Value}));
            NoteVirtual(0, 7U, _O({sample.Value}));
            Note(0, 0, _O({sample.Value}));
            """);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[5];
        plan.Source.Render(5, pcm);
        Assert.That(pcm[0], Is.EqualTo(1f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.5f).Within(1e-5f),
            "the virtual host alone receives the delayed channel response");
    }

    [Test]
    public void DataPatternNativeFilterAssignmentTextRendersWithSongSelectionAndChunkParity()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = song.AllocateObjectId();
        DataPatternDefinition pattern = new(root, "Filter text")
        { ChannelCount = 1, RowCount = 2 };
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Note = new StartPatternNote(sample);
        cell.Effects.Add(new SetChannelFilterPatternEffect(filter.DefinitionId));
        cell.Effects.Add(new SetChannelFilterAssignmentsPatternEffect(
            "# echo: set a new structural length\necho.DelaySeconds = 0.002",
            filter.DefinitionId));
        song.Add(pattern);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan split = factory.Create(song, root);
        float[] pcm = new float[8], chunks = new float[8];
        whole.Source.Render(8, pcm);
        split.Source.Render(1, chunks.AsSpan(0, 1));
        split.Source.Render(7, chunks.AsSpan(1));
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(0.5f).Within(1e-5f),
            "the command sets the delay to two frames before first PCM");
        Assert.That(pcm[2], Is.EqualTo(1f).Within(1e-5f));
        Assert.That(chunks, Is.EqualTo(pcm).Within(1e-6f));
    }

    [Test]
    public void DataPatternMixedTextCommitsValidFieldWithoutRuntimeDiagnostics()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId root = song.AllocateObjectId();
        DataPatternDefinition pattern = new(root, "Invalid Filter text")
        { RowCount = 2, ChannelCount = 1 };
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Note = new StartPatternNote(sample);
        cell.Effects.Add(new SetChannelFilterPatternEffect(filter.DefinitionId));
        cell.Effects.Add(new SetChannelFilterAssignmentsPatternEffect(
            "echo.Feedforward = 0\necho.NoSuchParameter = 9",
            filter.DefinitionId));
        song.Add(pattern);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[8];
        plan.Source.Render(8, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.LessThan(1f),
            "the valid Feedforward change must be published despite a bad sibling");
        Assert.That(plan.SequencingContext.Diagnostics.Drain()
            .Count(x => x.Code == "HRSEQ009"), Is.Zero,
            "individual invalid text assignments must be silent at runtime");
    }

    private static FilterInstance Echo(SongDocument song, double delay)
    {
        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition definition = new(filterId, "Tone echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]);
        // Script/data note starts select a shared Filter *definition*, not
        // arbitrary overrides on a different, unreferenced use. Configure
        // the definition's defaults to test the selected audible response.
        definition.SetStageDefaults(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(delay)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        song.Add(definition);
        return new FilterInstance(definition);
    }

    private static NodeEnvelopeDefinition HalfEnvelope(ObjectId id)
    {
        NodeEnvelopeDefinition envelope = new(id, "Half");
        envelope.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.5)],
            0, 0, EnvelopeSustainLoopMode.Forward);
        return envelope;
    }

    [Test]
    public void ScriptSelectedNestedPatternRendersWithInheritedEnvelopeAndChunkParity()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId child = ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));");
        ObjectId envelope = song.AllocateObjectId();
        song.Add(HalfEnvelope(envelope));
        ObjectId inst = song.AllocateObjectId();
        ScriptInstrumentDefinition definition = new(inst, "Recursive selector")
        { Source = "pitchMultiplier >= 1.0 ? 0 : -1" };
        definition.ToneSpecifications.Add(new ToneSpecification
        { SourceId = child, VolumeEnvelopeId = envelope });
        song.Add(definition);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({inst.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan chunked = factory.Create(song, root);
        float[] first = new float[70], second = new float[70];
        whole.Source.Render(first.Length, first);
        for (int pos = 0; pos < second.Length;)
        {
            int count = Math.Min(second.Length - pos, 3);
            chunked.Source.Render(count, second.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(first[5], Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(second, Is.EqualTo(first).Within(1e-6f));
        });
    }

    [Test]
    public void ScriptInstrumentRejectsSelectedSelfCycle()
    {
        SongDocument song = new();
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Cycle")
        { Source = "0" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = id });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        Assert.Throws<InvalidOperationException>(() =>
            plan.Source.Render(2, new float[2]));
    }

    [Test]
    public void DynamicPitchExpressionModulatesActualSamplePlaybackFrequency()
    {
        SongDocument song = new();
        ObjectId sample = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(
            sample, "Ramp", "ramp.wav", RampWave()));
        ObjectId fixedId = song.AllocateObjectId();
        ScriptInstrumentDefinition fixedInstrument = new(fixedId, "Fixed")
        { Source = "0", PitchExpression = "1.0" };
        fixedInstrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(fixedInstrument);
        ObjectId dynamicId = song.AllocateObjectId();
        ScriptInstrumentDefinition dynamicInstrument = new(dynamicId, "Double")
        { Source = "0", PitchExpression = "pitchMultiplier * 2.0" };
        dynamicInstrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(dynamicInstrument);
        ObjectId fixedRoot = ScriptPattern(song,
            $"Note(0, 0, _O({fixedId.Value}));");
        ObjectId dynamicRoot = ScriptPattern(song,
            $"Note(0, 0, _O({dynamicId.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan ordinary = factory.Create(song, fixedRoot);
        using PreparedIncrementalPlaybackPlan shifted = factory.Create(song, dynamicRoot);
        float[] plain = new float[30];
        float[] pitched = new float[30];
        ordinary.Source.Render(30, plain);
        shifted.Source.Render(30, pitched);
        Assert.Multiple(() =>
        {
            Assert.That(plain[10], Is.GreaterThan(0.0f));
            Assert.That(pitched[10], Is.EqualTo(plain[10] * 2.0f).Within(1e-5f));
            Assert.That(pitched[20], Is.EqualTo(plain[20] * 2.0f).Within(1e-5f));
        });
    }

    [Test]
    public void NonPositiveDynamicPitchScaleFailsBeforeBindingSound()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Bad pitch")
        { Source = "0", PitchExpression = "0.0" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        InvalidOperationException? error = Assert.Throws<InvalidOperationException>(
            () => plan.Source.Render(2, new float[2]));
        Assert.That(error!.Message, Does.Contain("invalid pitch multiplier"));
    }

    [Test]
    public void InvalidIndexFailsExplicitlyAndNotAsAnArbitrarySource()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Bad index")
        { Source = "7" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        InvalidOperationException? error = Assert.Throws<InvalidOperationException>(
            () => plan.Source.Render(2, new float[2]));
        Assert.That(error!.Message, Does.Contain("invalid tone index"));
    }

    private static ObjectId ScriptPattern(SongDocument song, string source)
    {
        ObjectId id = song.AllocateObjectId();
        song.Add(new ScriptPatternDefinition(id, "Notes")
        { Source = source, RowCount = 3, ChannelCount = 1 });
        return id;
    }

    private static ObjectId Sample(SongDocument song, short amplitude)
    {
        ObjectId id = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(id, "PCM", "sample.wav",
            Wave(amplitude)));
        return id;
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(
            Vector3.Zero, positionalImportance: 0)]);

    private static byte[] RampWave()
    {
        const int frames = 256;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 2 * frames);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(2 * frames);
        for (int i = 0; i < frames; i++) writer.Write((short)(i * 100));
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Wave(short amplitude)
    {
        const int frames = 1000;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 2 * frames);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(2 * frames);
        for (int i = 0; i < frames; i++) writer.Write(amplitude);
        writer.Flush();
        return stream.ToArray();
    }
}
