using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectDuplicationTests
{
    [Test]
    public void PrepareIsDetachedAndPreservesScriptedPatternFieldsAndSourceColour()
    {
        SongDocument document = new();
        ScriptPatternDefinition source = new(document.AllocateObjectId(), "Script")
        {
            Source = "return 3;",
            RowCount = 28,
            ChannelCount = 3,
            MinorHighlightRows = 2,
            MajorHighlightRows = 8,
        };
        document.Add(source);
        ObjectId next = document.NextUnallocatedObjectId;
        uint audio = document.AudioRevision;
        uint revision = document.DocumentRevision;
        string snapshot = SongDocumentJson.SerializeAuthoringSnapshot(document);

        SongObjectDuplicateDraft draft =
            SongObjectDuplication.Prepare(document, source.Id);
        ScriptPatternDefinition duplicate =
            (ScriptPatternDefinition)draft.Candidate;

        Assert.Multiple(() =>
        {
            Assert.That(draft.ExpectedId, Is.EqualTo(next));
            Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));
            Assert.That(document.DocumentRevision, Is.EqualTo(revision));
            Assert.That(document.AudioRevision, Is.EqualTo(audio));
            Assert.That(document.Objects.Count, Is.EqualTo(1));
            Assert.That(duplicate.Id, Is.EqualTo(next));
            Assert.That(duplicate.Name, Is.EqualTo("Script 2"));
            Assert.That(duplicate.Source, Is.EqualTo(source.Source));
            Assert.That(duplicate.RowCount, Is.EqualTo(28));
            Assert.That(duplicate.ChannelCount, Is.EqualTo(3));
            Assert.That(duplicate.MinorHighlightRows, Is.EqualTo(2));
            Assert.That(duplicate.MajorHighlightRows, Is.EqualTo(8));
            Assert.That(duplicate.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
            Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(document),
                Is.EqualTo(snapshot));
        });

        duplicate.Source = "return 9;";
        Assert.That(source.Source, Is.EqualTo("return 3;"));
    }

    [Test]
    public void DataPatternCellsAndNoteAndEffectRecordsAreIndependent()
    {
        SongDocument document = new();
        DataPatternDefinition source = new(document.AllocateObjectId(), "Notes")
        {
            RowCount = 8,
            ChannelCount = 2,
            MinorHighlightRows = 2,
            MajorHighlightRows = 4,
        };
        PatternCell sourceCell = source.Grid.GetOrCreateCell(3, 1);
        sourceCell.Note = new StartPatternNote(
            new ObjectId(42), pitchMultiplier: 1.25);
        sourceCell.SourceId = new ObjectId(42);
        sourceCell.Volume = .35;
        sourceCell.Effects.Add(new TrackerSpeedPatternEffect(12));
        document.Add(source);

        DataPatternDefinition clone = (DataPatternDefinition)
            SongObjectDuplication.Prepare(document, source.Id).Candidate;
        PatternCell copiedCell = clone.Grid[3, 1]!;

        Assert.That(copiedCell, Is.Not.SameAs(sourceCell));
        Assert.That(copiedCell.Note, Is.EqualTo(sourceCell.Note));
        Assert.That(copiedCell.Note, Is.Not.SameAs(sourceCell.Note));
        Assert.That(copiedCell.Effects[0], Is.EqualTo(sourceCell.Effects[0]));
        Assert.That(copiedCell.Effects[0], Is.Not.SameAs(sourceCell.Effects[0]));
        Assert.That(copiedCell.SourceId, Is.EqualTo(sourceCell.SourceId));
        Assert.That(copiedCell.Volume, Is.EqualTo(.35));
        Assert.That(clone.RowCount, Is.EqualTo(8));
        Assert.That(clone.ChannelCount, Is.EqualTo(2));
        Assert.That(clone.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
        copiedCell.SourceId = ObjectId.None;
        copiedCell.Effects.Clear();
        copiedCell.Note = new PatternNoteCut();
        Assert.That(sourceCell.SourceId, Is.EqualTo(new ObjectId(42)));
        Assert.That(sourceCell.Effects.Count, Is.EqualTo(1));
        Assert.That(sourceCell.Note, Is.TypeOf<StartPatternNote>());
    }

    [Test]
    public void SequenceEntriesKeepExistingSongReferencesButDoNotShareCollection()
    {
        SongDocument document = new();
        DataSequenceDefinition source =
            new(document.AllocateObjectId(), "Sequence");
        source.Entries.Add(new SequenceEntry(new ObjectId(13), 7));
        document.Add(source);

        DataSequenceDefinition copy = (DataSequenceDefinition)
            SongObjectDuplication.Prepare(document, source.Id).Candidate;
        Assert.That(copy.Entries.Count, Is.EqualTo(1));
        Assert.That(copy.Entries[0], Is.EqualTo(source.Entries[0]));
        Assert.That(copy.Entries[0], Is.Not.SameAs(source.Entries[0]));
        copy.Entries.Clear();
        Assert.That(source.Entries.Count, Is.EqualTo(1));
    }

    [Test]
    public void ScriptSequenceCopiesTheExpressionWithoutChangingSource()
    {
        SongDocument document = new();
        ScriptSequenceDefinition source =
            new(document.AllocateObjectId(), "Script sequence")
            { Source = "return 5;" };
        document.Add(source);
        ScriptSequenceDefinition copy = (ScriptSequenceDefinition)
            SongObjectDuplication.Prepare(document, source.Id).Candidate;
        Assert.That(copy.Source, Is.EqualTo("return 5;"));
        copy.Source = "return -1;";
        Assert.That(source.Source, Is.EqualTo("return 5;"));
    }

    [Test]
    public void NodeEnvelopeCopiesCurveSustainAndViewport()
    {
        SongDocument document = new();
        NodeEnvelopeDefinition source = new(
            document.AllocateObjectId(), "ADSR");
        source.UpdateDefinition(
            [new(TimeSpan.Zero, -.25), new(TimeSpan.FromSeconds(2), .8)],
            1, 1, EnvelopeSustainLoopMode.PingPong, -.5, 1.5);
        document.Add(source);

        NodeEnvelopeDefinition copy = (NodeEnvelopeDefinition)
            SongObjectDuplication.Prepare(document, source.Id).Candidate;
        Assert.That(copy.Nodes, Is.EqualTo(source.Nodes));
        Assert.That(copy.Nodes, Is.Not.SameAs(source.Nodes));
        Assert.That(copy.SustainStartIndex, Is.EqualTo(1));
        Assert.That(copy.SustainEndIndex, Is.EqualTo(1));
        Assert.That(copy.SustainLoopMode, Is.EqualTo(EnvelopeSustainLoopMode.PingPong));
        Assert.That(copy.Vmin, Is.EqualTo(-.5));
        Assert.That(copy.Vmax, Is.EqualTo(1.5));
        copy.ReplaceCurve(
            [new(TimeSpan.Zero, 10)], 0, 0,
            EnvelopeSustainLoopMode.Forward);
        Assert.That(source.Nodes.Count, Is.EqualTo(2));
    }

    [Test]
    public void FilterCopiesStageIdentityAndHistoricalGenerationState()
    {
        SongDocument document = new();
        FilterDefinition source = new(
            document.AllocateObjectId(), "Filter");
        source.AddStage("second", FilterKind.OnePole);
        source.RemoveStage(2); // Historical stage identity must survive.
        document.Add(source);
        FilterDefinition copy = (FilterDefinition)
            SongObjectDuplication.Prepare(document, source.Id).Candidate;

        Assert.That(copy.Stages.Count, Is.EqualTo(source.Stages.Count));
        Assert.That(copy.Stages[0], Is.Not.SameAs(source.Stages[0]));
        Assert.That(copy.Stages[0].DefaultParameters,
            Is.EqualTo(source.Stages[0].DefaultParameters));
        Assert.That(copy.NextStageIdForPersistence,
            Is.EqualTo(source.NextStageIdForPersistence));
        Assert.That(copy.StageGenerationsForPersistence,
            Is.EquivalentTo(source.StageGenerationsForPersistence));
        copy.AddStage("independent", FilterKind.OnePole);
        Assert.That(source.Stages.Count, Is.EqualTo(1));
    }

    [Test]
    public void SuffixIncrementsExistingTerminalNumberAndSkipsOccupiedNames()
    {
        SongDocument document = new();
        ScriptSequenceDefinition source =
            new(document.AllocateObjectId(), "New Sequence 3");
        document.Add(source);
        document.Add(new ScriptSequenceDefinition(
            document.AllocateObjectId(), "NEW SEQUENCE 4"));
        document.Add(new ScriptSequenceDefinition(
            document.AllocateObjectId(), "New Sequence 6"));
        Assert.That(SongObjectDuplication.SuggestName(document, source),
            Is.EqualTo("New Sequence 5"));

        ScriptSequenceDefinition plain = new(
            document.AllocateObjectId(), "Plain");
        document.Add(plain);
        document.Add(new ScriptSequenceDefinition(
            document.AllocateObjectId(), "PLAIN 2"));
        Assert.That(SongObjectDuplication.SuggestName(document, plain),
            Is.EqualTo("Plain 3"));
    }

    [Test]
    public void CommitPublishesOneNewIdentityWithGuardedWholeObjectUndoRedo()
    {
        SongDocument document = new();
        ScriptEnvelopeDefinition source =
            new(document.AllocateObjectId(), "Release")
            {
                Source = "releaseSeconds",
                ReleaseDuration = TimeSpan.FromSeconds(1),
            };
        document.Add(source);
        SongObjectDuplicateDraft draft =
            SongObjectDuplication.Prepare(document, source.Id);
        uint before = document.AudioRevision;

        SongImportBatchEdit receipt = SongObjectDuplication.Commit(draft);
        Assert.That(receipt.ImportedObjectIds.Single(),
            Is.EqualTo(draft.ExpectedId));
        Assert.That(document.AudioRevision, Is.EqualTo(before + 1));
        Assert.That(document.Objects.Count, Is.EqualTo(2));
        ScriptEnvelopeDefinition copy =
            (ScriptEnvelopeDefinition)document.Objects[draft.ExpectedId];
        Assert.That(copy.Source, Is.EqualTo(source.Source));
        Assert.That(copy.ReleaseDuration, Is.EqualTo(source.ReleaseDuration));
        Assert.That(copy.Name, Is.EqualTo("Release 2"));
        Assert.That(document.GetSectionRoot(SongTreeSection.Envelopes)
            .Children.OfType<SongTreeObject>()
            .Count(n => n.ObjectId == copy.Id), Is.EqualTo(1));
        receipt.Undo();
        Assert.That(document.Objects.ContainsKey(copy.Id), Is.False);
        Assert.That(document.Tombstones.ContainsKey(copy.Id), Is.False);
        receipt.Redo();
        Assert.That(document.Objects[copy.Id], Is.SameAs(copy));
        Assert.That(document.Objects[source.Id], Is.SameAs(source));
    }

    [Test]
    public void StaleDraftRefusesAtomicallyWithoutConsumingAnId()
    {
        SongDocument document = new();
        NodeEnvelopeDefinition source =
            new(document.AllocateObjectId(), "Original");
        document.Add(source);
        SongObjectDuplicateDraft draft =
            SongObjectDuplication.Prepare(document, source.Id);
        source.SetDisplayRange(-1.0, 2.0); // Unmarked authored change.
        ObjectId next = document.NextUnallocatedObjectId;
        uint revision = document.DocumentRevision;
        Assert.That(() => SongObjectDuplication.Commit(draft),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(document.Objects.Count, Is.EqualTo(1));
        Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(document.DocumentRevision, Is.EqualTo(revision));
    }

    [Test]
    public void UnsupportedFamilyRejectsBeforeAnyAllocationOrDocumentMutation()
    {
        SongDocument document = new();
        InstrumentDefinition instrument =
            new(document.AllocateObjectId(), "Instrument");
        document.Add(instrument);
        ObjectId next = document.NextUnallocatedObjectId;
        uint revision = document.DocumentRevision;

        Assert.That(() => SongObjectDuplication.Prepare(document, instrument.Id),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(document.DocumentRevision, Is.EqualTo(revision));
        Assert.That(document.Objects.Count, Is.EqualTo(1));
    }
}
