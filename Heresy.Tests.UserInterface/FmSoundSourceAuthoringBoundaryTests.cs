using System;
using System.Linq;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSoundSourceAuthoringBoundaryTests
{
    [Test]
    public void RecursiveSourceIsRejectedBeforeGraphOrAudioRevisionChanges()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition a = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "A");
        FmSynthDefinition b = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "B");
        FmSynthDocumentEditor.UpdateNode(workspace, b,
            new FmSoundSourceNode(0, a.Id, 440.0));
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        FmSynthGraph before = a.Graph;

        Assert.That(() => FmSynthDocumentEditor.UpdateNode(workspace, a,
            new FmSoundSourceNode(0, b.Id, 440.0)),
            Throws.InvalidOperationException.With.Message.Contains("cycle"));
        Assert.That(a.Graph, Is.SameAs(before));
        Assert.That(a.Graph.Nodes.Single(), Is.TypeOf<FmConstantNode>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
        Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(document));
    }

    [Test]
    public void ReferencedSoundIdDoesNotNeedToBePresentUntilPreparation()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Partial");
        FmSynthDocumentEditor.UpdateNode(workspace, synth,
            new FmSoundSourceNode(0, ObjectId.None, 440.0));
        Assert.That(synth.Graph.Nodes.Single(),
            Is.TypeOf<FmSoundSourceNode>());
    }
}
