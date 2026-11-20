using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectDeepDuplicationD4Tests
{
    [Test]
    public void TwoScriptedObjectsAndSampleFormCycleWithRepeatedTargetButSingleClone()
    {
        SongDocument song = new();
        ScriptSequenceDefinition sequence =
            new(song.AllocateObjectId(), "Sequence");
        ScriptPatternDefinition pattern =
            new(song.AllocateObjectId(), "Pattern");
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Audio", "audio.wav", Wave());
        sequence.Source =
            $"var p = _O({pattern.Id.Value});"
            + $" var a = _O({sample.Id.Value});"
            + $" var b = _O({sample.Id.Value}); // _O(91)";
        pattern.Source = $"_O({sequence.Id.Value});";
        song.Add(sequence);
        song.Add(pattern);
        song.Add(sample);

        uint revision = song.DocumentRevision;
        ObjectId next = song.NextUnallocatedObjectId;
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, sequence.Id);

        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(draft.Candidates, Has.Count.EqualTo(3));
        Assert.That(draft.IdMap.Keys, Is.EquivalentTo(
            new[] { sequence.Id, pattern.Id, sample.Id }));
        ScriptSequenceDefinition newSequence = (ScriptSequenceDefinition)draft.RootCandidate;
        ScriptPatternDefinition newPattern = (ScriptPatternDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[pattern.Id]);
        Assert.That(newSequence.Source, Does.Contain(
            $"_O({newPattern.Id.Value})"));
        Assert.That(newSequence.Source.Split(
            $"_O({draft.IdMap[sample.Id].Value})").Length,
            Is.EqualTo(3), "both sample uses must point to one duplicate");
        Assert.That(newSequence.Source, Does.Contain("// _O(91)"));
        Assert.That(newPattern.Source,
            Is.EqualTo($"_O({newSequence.Id.Value});"));
        Assert.That(sequence.Source, Does.Contain(
            $"_O({pattern.Id.Value})"));
        Assert.That(pattern.Source,
            Is.EqualTo($"_O({sequence.Id.Value});"));

        SongImportBatchEdit receipt = SongObjectDeepDuplication.Commit(draft);
        Assert.That(receipt.ImportedObjectIds, Has.Count.EqualTo(3));
        Assert.That(song.Objects.Count, Is.EqualTo(6));
        receipt.Undo();
        Assert.That(song.Objects.Count, Is.EqualTo(3));
        receipt.Redo();
        Assert.That(song.Objects[newSequence.Id], Is.SameAs(newSequence));
    }

    [Test]
    public void ScriptInstrumentTraversesAllToneReferencesAndAllThreeScriptFields()
    {
        SongDocument song = new();
        SampleDefinition source = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sample", "s.wav", Wave());
        song.Add(source);
        NodeEnvelopeDefinition envelope =
            new(song.AllocateObjectId(), "Envelope");
        song.Add(envelope);
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        ScriptInstrumentDefinition script = new(
            song.AllocateObjectId(), "Script Instrument")
        {
            Source = $"_O({source.Id.Value});",
            PitchExpression = $"_O({envelope.Id.Value});",
            NnaExpression = $"_O({filter.Id.Value});",
        };
        script.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = source.Id,
            VolumeEnvelopeId = envelope.Id,
            PitchEnvelopeId = envelope.Id,
            FilterEnvelopeId = envelope.Id,
            PanningEnvelopeId = envelope.Id,
            FilterConfiguration = new FilterInstance(filter),
        });
        song.Add(script);

        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, script.Id);
        Assert.That(draft.IdMap.Keys, Is.EquivalentTo(
            new[] { script.Id, source.Id, envelope.Id, filter.Id }));
        ScriptInstrumentDefinition copy =
            (ScriptInstrumentDefinition)draft.RootCandidate;
        Assert.That(copy.Source, Is.EqualTo(
            $"_O({draft.IdMap[source.Id].Value});"));
        Assert.That(copy.PitchExpression, Is.EqualTo(
            $"_O({draft.IdMap[envelope.Id].Value});"));
        Assert.That(copy.NnaExpression, Is.EqualTo(
            $"_O({draft.IdMap[filter.Id].Value});"));
        ToneSpecification tone = copy.ToneSpecifications.Single();
        Assert.That(tone.SourceId, Is.EqualTo(draft.IdMap[source.Id]));
        Assert.That(tone.VolumeEnvelopeId,
            Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(tone.PitchEnvelopeId,
            Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(tone.FilterEnvelopeId,
            Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(tone.PanningEnvelopeId,
            Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(tone.FilterConfiguration!.Definition.Id,
            Is.EqualTo(draft.IdMap[filter.Id]));
        Assert.That(script.Source,
            Is.EqualTo($"_O({source.Id.Value});"));
        Assert.That(script.ToneSpecifications[0].SourceId, Is.EqualTo(source.Id));
        SongObjectDeepDuplication.Commit(draft);
        Assert.That(song.Objects.Count, Is.EqualTo(8));
    }

    [Test]
    public void ScriptEnvelopeReferencesAreTraversedAndReleaseDurationIsPreserved()
    {
        SongDocument song = new();
        ScriptEnvelopeDefinition baseEnvelope = new(
            song.AllocateObjectId(), "Base");
        song.Add(baseEnvelope);
        ScriptEnvelopeDefinition root = new(
            song.AllocateObjectId(), "Root")
        {
            Source = $"_O({baseEnvelope.Id.Value})",
            ReleaseDuration = TimeSpan.FromSeconds(1.5),
        };
        song.Add(root);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, root.Id);
        Assert.That(draft.IdMap.Count, Is.EqualTo(2));
        ScriptEnvelopeDefinition clone =
            (ScriptEnvelopeDefinition)draft.RootCandidate;
        Assert.That(clone.ReleaseDuration, Is.EqualTo(root.ReleaseDuration));
        Assert.That(clone.Source, Is.EqualTo(
            $"_O({draft.IdMap[baseEnvelope.Id].Value})"));
        Assert.That(root.Source,
            Is.EqualTo($"_O({baseEnvelope.Id.Value})"));
    }

    [Test]
    public void ScriptReachableDataPatternRemapsReferencesAlreadyInClosure()
    {
        SongDocument song = new();
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sound", "a.wav", Wave());
        song.Add(sample);
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        DataPatternDefinition pattern =
            new(song.AllocateObjectId(), "Pattern");
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.SourceId = sample.Id;
        cell.Note = new StartPatternNote(eventFilterDefinitionId: filter.Id);
        song.Add(pattern);
        ScriptSequenceDefinition sequence =
            new(song.AllocateObjectId(), "Script sequence")
        {
            Source = $"_O({pattern.Id.Value});"
                + $" _O({sample.Id.Value});"
                + $" _O({filter.Id.Value});",
        };
        song.Add(sequence);

        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, sequence.Id);
        DataPatternDefinition newPattern = (DataPatternDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[pattern.Id]);
        PatternCell newCell = newPattern.Grid[0, 0]!;
        Assert.That(newCell.SourceId, Is.EqualTo(draft.IdMap[sample.Id]));
        Assert.That(((StartPatternNote)newCell.Note!).EventFilterDefinitionId,
            Is.EqualTo(draft.IdMap[filter.Id]));
        Assert.That(pattern.Grid[0, 0]!.SourceId, Is.EqualTo(sample.Id));
        Assert.That(draft.IdMap.Count, Is.EqualTo(4));
    }

    [Test]
    public void ScriptWithNoReferencesStillAllowsDeepDuplication()
    {
        SongDocument song = new();
        ScriptPatternDefinition pattern = new(song.AllocateObjectId(), "Empty")
        {
            Source = "var i = 42;",
        };
        song.Add(pattern);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, pattern.Id);
        Assert.That(draft.IdMap.Count, Is.EqualTo(1));
        Assert.That(((ScriptPatternDefinition)draft.RootCandidate).Source,
            Is.EqualTo(pattern.Source));
        Assert.That(draft.RootCandidate.Id, Is.Not.EqualTo(pattern.Id));
    }

    [TestCase("_O(1 + 2);")]
    [TestCase("_O();")]
    [TestCase("var value = _O(7")]
    [TestCase("_O(0);")]
    [TestCase("_O(999);")]
    public void MalformedOrMissingScriptTargetRejectsEntirePlan(string source)
    {
        SongDocument song = new();
        ScriptSequenceDefinition sequence =
            new(song.AllocateObjectId(), "Scripted")
            { Source = source };
        song.Add(sequence);
        uint revision = song.DocumentRevision;
        ObjectId cursor = song.NextUnallocatedObjectId;
        Assert.That(() => SongObjectDeepDuplication.Prepare(song, sequence.Id),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
    }

    [Test]
    public void ShadowedFunctionAndCommentsDoNotCauseMissingDependency()
    {
        SongDocument song = new();
        ScriptPatternDefinition pattern =
            new(song.AllocateObjectId(), "Shadow")
            {
                Source = """
                    object _O(uint id) => id;
                    var result = _O(999);
                    // _O(888)
                    var text = "_O(777)";
                    """,
            };
        song.Add(pattern);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, pattern.Id);
        Assert.That(draft.IdMap.Count, Is.EqualTo(1));
        Assert.That(((ScriptPatternDefinition)draft.RootCandidate).Source,
            Is.EqualTo(pattern.Source));
    }

    [Test]
    public void SelfReferencingScriptRootMapsBackToItsOwnDuplicate()
    {
        SongDocument song = new();
        ScriptSequenceDefinition script =
            new(song.AllocateObjectId(), "Self")
            { Source = "_O(1); _O(1);" };
        song.Add(script);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, script.Id);
        Assert.That(draft.Candidates, Has.Count.EqualTo(1));
        Assert.That(((ScriptSequenceDefinition)draft.RootCandidate).Source,
            Is.EqualTo($"_O({draft.RootCandidate.Id.Value});"
                + $" _O({draft.RootCandidate.Id.Value});"));
    }

    private static byte[] Wave()
    {
        using System.IO.MemoryStream memory = new();
        using (System.IO.BinaryWriter writer = new(memory,
            System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(40);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(4);
            writer.Write((short)4096);
            writer.Write((short)-8192);
        }
        return memory.ToArray();
    }
}
