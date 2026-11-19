using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class UnusedInvalidScriptPreviewTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void UnreferencedInvalidSequenceDoesNotStopSynthPreview(bool empty)
    {
        SongDocument document = new();
        ObjectId synthId = document.AllocateObjectId();
        document.Add(new FmSynthDefinition(synthId, "Synth",
            new FmSynthGraph([new FmConstantNode(0, 0.25)], 0)));
        ObjectId unused = document.AllocateObjectId();
        document.Add(new ScriptSequenceDefinition(unused, "Unused invalid")
        {
            Source = empty ? "" : "if (sequenceIndex == 0) return null;",
        });

        NoteScheduleBuilder schedule = new();
        schedule.Append(new NoteEvent(MusicalTime.Zero,
            ChannelTarget.Physical(0), [new StartNoteCommand(synthId)]));
        using PreparedIncrementalPlaybackPlan preview =
            Factory().CreateAdHoc(SongDocumentSnapshot.Create(document),
                schedule.Freeze());
        float[] pcm = new float[8];
        preview.Source.Render(pcm.Length, pcm);
        Assert.That(pcm, Is.All.EqualTo(0.25f).Within(1e-6f));
    }

    [Test]
    public void UnreferencedInvalidScriptPatternCannotBreakValidSequencePlayback()
    {
        SongDocument document = new();
        ObjectId patternId = document.AllocateObjectId();
        document.Add(new DataPatternDefinition(patternId, "Empty")
        {
            RowCount = 1, ChannelCount = 1,
        });
        ObjectId badId = document.AllocateObjectId();
        document.Add(new ScriptPatternDefinition(badId, "Unsafe")
        {
            Source = "System.IO.File.ReadAllText(\"nope\");",
        });
        using PreparedIncrementalPlaybackPlan playback =
            Factory().Create(document, patternId);
        float[] pcm = new float[2];
        Assert.DoesNotThrow(() => playback.Source.Render(pcm.Length, pcm));
        Assert.That(pcm, Is.All.Zero);
    }

    [Test]
    public void BrokenSequenceStillFailsIfActuallySelectedForPlayback()
    {
        SongDocument document = new();
        ObjectId brokenId = document.AllocateObjectId();
        document.Add(new ScriptSequenceDefinition(brokenId, "Broken")
        {
            Source = "if (sequenceIndex == 0) return null;",
        });
        NotSupportedException? exception = Assert.Throws<NotSupportedException>(
            () => Factory().Create(document, brokenId));
        Assert.That(exception!.Message,
            Does.Contain("Incremental Sequence script compilation failed"));
        Assert.That(exception.Message, Does.Contain("CS0161"));
    }

    [Test]
    public void BrokenPatternStillFailsIfActuallySelectedForPlayback()
    {
        SongDocument document = new();
        ObjectId brokenId = document.AllocateObjectId();
        document.Add(new ScriptPatternDefinition(brokenId, "Broken")
        {
            Source = "System.IO.File.ReadAllText(\"nope\");",
        });
        Assert.That(() => Factory().Create(document, brokenId),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("Incremental Pattern script compilation failed"));
    }

    [Test]
    public void PreparationRetainsSourceSpecificFailureUntilThatSourceIsUsed()
    {
        SongDocument document = new();
        ObjectId invalid = document.AllocateObjectId();
        document.Add(new ScriptSequenceDefinition(invalid, "Broken")
        {
            Source = "if (sequenceIndex == 0) return null;",
        });
        ObjectId valid = document.AllocateObjectId();
        document.Add(new ScriptSequenceDefinition(valid, "Valid")
        {
            Source = "return null;",
        });
        PreparedRoslynIncrementalScriptSources prepared =
            new(SongDocumentSnapshot.Create(document));
        Assert.That(prepared.TryResolve(valid, out SongObject? validObject), Is.True);
        Assert.That(prepared.TryResolve(invalid, out SongObject? badObject), Is.True);
        Assert.DoesNotThrow(() =>
            prepared.CompileSequence((ScriptSequenceDefinition)validObject!));
        Assert.That(() =>
            prepared.CompileSequence((ScriptSequenceDefinition)badObject!),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("CS0161"));
    }

    private static PreparedIncrementalPlaybackFactory Factory() =>
        new(new RenderConfiguration(1000,
            [new OutputChannelConfiguration(Vector3.Zero,
                positionalImportance: 0.0)]));
}
