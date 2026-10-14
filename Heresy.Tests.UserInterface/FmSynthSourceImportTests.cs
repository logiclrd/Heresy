using System;
using System.IO;
using System.Linq;
using System.Text;

using AwesomeAssertions;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthSourceImportTests
{
    private const double C4 = 440 * 0.5946035575013605;

    [Test]
    public void NestedSynthAndBothInstrumentTypesShareOneRemappedSampleAndEnvelope()
    {
        SongDocument song = new();
        ObjectId sampleId = song.AllocateObjectId();
        SampleDefinition sample = SampleDefinition.CreateImported(
            sampleId, "Kick", "kick.wav", Wave());
        song.Add(sample);

        ObjectId envId = song.AllocateObjectId();
        NodeEnvelopeDefinition envelope = new(envId, "Attack");
        envelope.ReplaceCurve([
            new(TimeSpan.Zero, 0.0),
            new(TimeSpan.FromMilliseconds(5), 1.0),
        ], 1, 1, EnvelopeSustainLoopMode.Forward);
        song.Add(envelope);

        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filter = new(filterId, "Source LP");
        song.Add(filter);
        FilterInstance toneFilter = new(filter);
        toneFilter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(200));

        ObjectId instrumentId = song.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "Data")
        {
            Divisions = 12, Offset = 7,
            NnaOverride = InstrumentNnaOverride.Continue,
        };
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId,
            VolumeEnvelopeId = envId,
            FilterConfiguration = toneFilter,
        });
        instrument.ToneTable.Add(0);
        song.Add(instrument);

        ObjectId scriptId = song.AllocateObjectId();
        ScriptInstrumentDefinition scripted = new(scriptId, "Script")
        {
            Source = "0",
            PitchExpression = "pitchMultiplier",
        };
        scripted.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId,
            PitchEnvelopeId = envId,
        });
        song.Add(scripted);

        ObjectId nestedId = song.AllocateObjectId();
        FmSynthDefinition nested = new(nestedId, "Child",
            new FmSynthGraph([
                new FmSoundSourceNode(0, instrumentId, C4, constant: true),
            ], 0));
        song.Add(nested);
        ObjectId rootId = song.AllocateObjectId();
        FmSynthDefinition root = new(rootId, "Parent",
            new FmSynthGraph([
                new FmSoundSourceNode(0, nestedId, C4),
                new FmSoundSourceNode(1, scriptId, C4 * 2, constant: true),
                new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
                new FmFilterNode(3, 2, toneFilter),
                new FmEnvelopeNode(4, envId),
            ], 3));
        song.Add(root);

        DocumentWorkspace target = new();
        for (int i = 0; i < 50; i++)
            target.Document.AllocateObjectId();
        FmSynthDefinition copy = FmSynthDocumentEditor.ImportFromSong(target,
            new SongFmSynthImportSource("source.hm.json", song, [root, nested]),
            [root.Id]).Single();

        copy.Name.Should().Be("Parent");
        FmSynthDefinition clonedNested = target.Document.Objects.Values
            .OfType<FmSynthDefinition>().Single(x => x.Name == "Child");
        InstrumentDefinition clonedInstrument = target.Document.Objects.Values
            .OfType<InstrumentDefinition>().Single();
        ScriptInstrumentDefinition clonedScript = target.Document.Objects.Values
            .OfType<ScriptInstrumentDefinition>().Single();
        SampleDefinition clonedSample = target.Document.Objects.Values
            .OfType<SampleDefinition>().Single();
        NodeEnvelopeDefinition clonedEnvelope = target.Document.Objects.Values
            .OfType<NodeEnvelopeDefinition>().Single();
        FilterDefinition clonedFilter = target.Document.Objects.Values
            .OfType<FilterDefinition>().Single();

        copy.Id.Should().NotBe(rootId);
        clonedNested.Id.Should().NotBe(nestedId);
        clonedSample.Id.Should().NotBe(sampleId);
        copy.SourceColourArgb.Should().Be(root.SourceColourArgb);
        clonedNested.SourceColourArgb.Should().Be(nested.SourceColourArgb);
        clonedSample.SourceColourArgb.Should().Be(sample.SourceColourArgb);
        clonedInstrument.SourceColourArgb.Should().Be(instrument.SourceColourArgb);
        clonedScript.SourceColourArgb.Should().Be(scripted.SourceColourArgb);
        target.Document.AudioRevision.Should().Be(target.Document.DocumentRevision,
            "colour-preserving imports create normal song objects without an extra revision");
        clonedSample.PendingAsset.Should().NotBeNull();
        clonedSample.PendingAsset!.Bytes.ToArray().Should().Equal(Wave());
        clonedSample.Asset.Should().BeNull(
            "the destination must own new encoded data, not reference the source song");
        clonedInstrument.ToneSpecifications.Single().SourceId
            .Should().Be(clonedSample.Id);
        clonedScript.ToneSpecifications.Single().SourceId
            .Should().Be(clonedSample.Id);
        clonedInstrument.ToneSpecifications.Single().VolumeEnvelopeId
            .Should().Be(clonedEnvelope.Id);
        clonedScript.ToneSpecifications.Single().PitchEnvelopeId
            .Should().Be(clonedEnvelope.Id);
        clonedInstrument.ToneSpecifications.Single().FilterConfiguration!
            .Definition.Should().BeSameAs(clonedFilter);
        copy.Graph.Nodes.OfType<FmFilterNode>().Single()
            .Configuration!.Definition.Should().BeSameAs(clonedFilter);
        copy.Graph.Nodes.OfType<FmEnvelopeNode>().Single().EnvelopeId
            .Should().Be(clonedEnvelope.Id);
        copy.Graph.Nodes.OfType<FmSoundSourceNode>().First()
            .SourceId.Should().Be(clonedNested.Id);
        copy.Graph.Nodes.OfType<FmSoundSourceNode>().Last()
            .SourceId.Should().Be(clonedScript.Id);
        copy.Graph.Nodes.OfType<FmSoundSourceNode>().Last()
            .Constant.Should().BeTrue();
        clonedNested.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().SourceId.Should().Be(clonedInstrument.Id);
        clonedNested.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().Constant.Should().BeTrue();
        clonedInstrument.NnaOverride.Should().Be(InstrumentNnaOverride.Continue);
        clonedInstrument.Offset.Should().Be(7);
        target.Document.Objects.Should().HaveCount(7);
    }

    [Test]
    public void CyclicInstrumentAndSynthReferencesRejectWithoutDocumentMutation()
    {
        SongDocument song = new();
        ObjectId instrumentId = song.AllocateObjectId();
        ObjectId synthId = song.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "Cycle");
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = synthId });
        song.Add(instrument);
        FmSynthDefinition root = new(synthId, "Cycle Root",
            new FmSynthGraph([
                new FmSoundSourceNode(0, instrumentId, C4),
            ], 0));
        song.Add(root);
        DocumentWorkspace workspace = new();
        Action import = () => FmSynthDocumentEditor.ImportFromSong(workspace,
            new SongFmSynthImportSource("source.hm.json", song, [root]),
            [root.Id]);
        import.Should().Throw<InvalidOperationException>()
            .WithMessage("*cycle*");
        workspace.Document.Objects.Should().BeEmpty();
        workspace.Document.DocumentRevision.Should().Be(0);
    }

    [Test]
    public void MissingDependencyRejectsBeforeAddingImportedFiltersOrSamples()
    {
        SongDocument song = new();
        ObjectId synthId = song.AllocateObjectId();
        FmSynthDefinition root = new(synthId, "Missing",
            new FmSynthGraph([
                new FmSoundSourceNode(0, (ObjectId)9999U, C4),
            ], 0));
        song.Add(root);
        DocumentWorkspace workspace = new();
        Action import = () => FmSynthDocumentEditor.ImportFromSong(workspace,
            new SongFmSynthImportSource("source.hm.json", song, [root]),
            [synthId]);
        import.Should().Throw<InvalidOperationException>()
            .WithMessage("*9999*missing*");
        workspace.Document.Objects.Should().BeEmpty();
        workspace.Document.AudioRevision.Should().Be(0);
    }

    private static byte[] Wave()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(38);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(2);
        writer.Write((short)16384);
        writer.Flush();
        return stream.ToArray();
    }
}
