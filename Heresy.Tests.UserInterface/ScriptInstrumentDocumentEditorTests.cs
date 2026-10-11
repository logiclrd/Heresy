using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptInstrumentDocumentEditorTests
{
    [Test]
    public void AuthoringValidatesExpressionAndUsesCanonicalTreePlacement()
    {
        DocumentWorkspace workspace = new();
        uint before = workspace.Document.AudioRevision;
        ScriptInstrumentDefinition definition =
            ScriptInstrumentDocumentEditor.Create(workspace, "Selector",
                "pitchMultiplier < 1.0 ? -1 : 0");
        Assert.Multiple(() =>
        {
            Assert.That(definition.Kind, Is.EqualTo(SongObjectKind.Instrument));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 1));
            Assert.That(workspace.Document.GetSectionRoot(SongTreeSection.Instruments)
                .Children.OfType<SongTreeObject>().Single().ObjectId,
                Is.EqualTo(definition.Id));
        });
        uint unchanged = workspace.Document.AudioRevision;
        Assert.Throws<ArgumentException>(() =>
            ScriptInstrumentDocumentEditor.SetSource(workspace, definition,
                "System.Environment.TickCount"));
        Assert.That(definition.Source, Is.EqualTo(
            "pitchMultiplier < 1.0 ? -1 : 0"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged));

        Assert.Throws<ArgumentException>(() =>
            ScriptInstrumentDocumentEditor.SetExpressions(workspace, definition,
                "-1", "System.Environment.TickCount"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged));
        ScriptInstrumentDocumentEditor.SetExpressions(
            workspace, definition, "-1", "pitchMultiplier * 2.0");
        Assert.That(definition.PitchExpression, Is.EqualTo("pitchMultiplier * 2.0"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged + 1));
        ScriptInstrumentDocumentEditor.SetExpressions(
            workspace, definition, "-1", "pitchMultiplier * 2.0");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged + 1));
        // Compatibility helper still changes only the index expression.
        ScriptInstrumentDocumentEditor.SetSource(workspace, definition, "-1");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged + 1));
        ScriptInstrumentDocumentEditor.SetSource(workspace, definition, "-1");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(unchanged + 1));
    }

    [Test]
    public void DeclaredTonesValidateReferencesAndMutateAtomically()
    {
        DocumentWorkspace workspace = new();
        ObjectId sample = workspace.Document.AllocateObjectId();
        workspace.Document.Add(SampleDefinition.CreateImported(
            sample, "Sample", "source.wav", Wave()));
        ObjectId envelope = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new NodeEnvelopeDefinition(envelope, "Volume"));
        ScriptInstrumentDefinition instrument =
            ScriptInstrumentDocumentEditor.Create(workspace, "Picker", "0");
        uint baseline = workspace.Document.AudioRevision;

        Assert.Throws<InvalidOperationException>(() =>
            ScriptInstrumentDocumentEditor.AddTone(workspace, instrument,
                (ObjectId)123456U));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(baseline));
        Assert.That(instrument.ToneSpecifications, Is.Empty);
        int index = ScriptInstrumentDocumentEditor.AddTone(
            workspace, instrument, sample, 1.5, volumeEnvelopeId: envelope);
        Assert.That(index, Is.Zero);
        Assert.That(instrument.ToneSpecifications[0].VolumeEnvelopeId,
            Is.EqualTo(envelope));
        uint updated = workspace.Document.AudioRevision;
        ScriptInstrumentDocumentEditor.UpdateTone(
            workspace, instrument, 0, sample, 1.5, volumeEnvelopeId: envelope);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(updated));
        Assert.Throws<InvalidOperationException>(() =>
            ScriptInstrumentDocumentEditor.UpdateTone(
                workspace, instrument, 0, sample, 1.5,
                filterEnvelopeId: sample));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(updated));
        ScriptInstrumentDocumentEditor.RemoveTone(workspace, instrument, 0);
        Assert.That(instrument.ToneSpecifications, Is.Empty);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(updated + 1));
    }

    private static byte[] Wave()
    {
        const int frames = 8;
        using System.IO.MemoryStream stream = new();
        using System.IO.BinaryWriter writer = new(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 2 * frames);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(2 * frames);
        for (int i = 0; i < frames; i++) writer.Write((short)16384);
        writer.Flush();
        return stream.ToArray();
    }
}
