using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterAssignmentEnvelopeAuthoringTests
{
    private static (SongDocument Song, FilterDefinition Filter) Create()
    {
        SongDocument song = new();
        FilterDefinition definition = new(song.AllocateObjectId(), "Shared", [
            new FilterStageDefinition(1, "stage", FilterKind.BiquadLowPass),
        ]);
        song.Add(definition);
        song.Add(new NodeEnvelopeDefinition(song.AllocateObjectId(), "Pitch Envelope"));
        song.Add(new ScriptEnvelopeDefinition(song.AllocateObjectId(), "Script Envelope"));
        return (song, definition);
    }

    private static IReadOnlyDictionary<string, string> Fields(
        ObjectId filterId, string assignments)
        => new Dictionary<string, string>
        {
            ["filterId"] = filterId.Value.ToString(),
            ["assignments"] = assignments,
        };

    [Test]
    public void NoteAndChannelAuthoringPreserveMixedReferenceTextAndFilterHint()
    {
        var (song, filter) = Create();
        const string text = "stage.CutoffHz = @\"Pitch Envelope\"\nstage.Q = 0.8";
        foreach (PatternEffect seed in new PatternEffect[]
            {
                new SetNoteFilterAssignmentsPatternEffect("unused"),
                new SetChannelFilterAssignmentsPatternEffect("unused"),
            })
        {
            PatternEffect built = NativePatternEffectEditor.Rebuild(
                seed, Fields(filter.Id, text), song: song);
            string authored = built switch
            {
                SetNoteFilterAssignmentsPatternEffect n => n.AssignmentText,
                SetChannelFilterAssignmentsPatternEffect c => c.AssignmentText,
                _ => throw new Exception("Wrong effect"),
            };
            Assert.That(authored, Is.EqualTo(text));
            Assert.That(built is SetNoteFilterAssignmentsPatternEffect noteEffect
                ? noteEffect.AuthoringFilterId
                : ((SetChannelFilterAssignmentsPatternEffect)built).AuthoringFilterId,
                Is.EqualTo(filter.Id));
            Assert.That(NativePatternEffectEditor.Describe(built)
                .Fields.Single(f => f.Key == "assignments").Value,
                Is.EqualTo(text));
        }
    }

    [Test]
    public void AuthoringRejectsUnknownAndAmbiguousEnvelopesUnlessWarningsAcknowledged()
    {
        var (song, filter) = Create();
        var seed = new SetChannelFilterAssignmentsPatternEffect("");
        string unknown = "stage.CutoffHz = @\"Not In Song\"";
        Assert.That(() => NativePatternEffectEditor.Rebuild(
            seed, Fields(filter.Id, unknown), song: song),
            Throws.ArgumentException.With.Message.Contains("does not exist"));
        song.Add(new ScriptEnvelopeDefinition(song.AllocateObjectId(), "Pitch Envelope"));
        const string ambiguous = "stage.CutoffHz = @\"pitch envelope\"";
        Assert.That(() => NativePatternEffectEditor.Rebuild(
            seed, Fields(filter.Id, ambiguous), song: song),
            Throws.ArgumentException.With.Message.Contains("ambiguous"));

        // Unchanged two-click warning acknowledgement preserves the authored
        // text byte-for-byte, as in existing invalid-line handling.
        var saved = NativePatternEffectEditor.Rebuild(
            seed, Fields(filter.Id, ambiguous), song: song,
            allowInvalidFilterAssignments: true);
        Assert.That(((SetChannelFilterAssignmentsPatternEffect)saved)
            .AssignmentText, Is.EqualTo(ambiguous));
    }

    [Test]
    public void StructuralAndNonNumericEnvelopeReferencesRejectBeforePublishing()
    {
        var (song, filter) = Create();
        string invalid = "stage.CutoffHz = @\"Script Envelope\"\nstage.Q = 0.8";
        var result = NativePatternEffectEditor.Rebuild(
            new SetNoteFilterAssignmentsPatternEffect(""), Fields(filter.Id, invalid),
            song: song);
        Assert.That(result, Is.TypeOf<SetNoteFilterAssignmentsPatternEffect>());
        uint revision = song.AudioRevision;
        Assert.That(() => NativePatternEffectEditor.Rebuild(
            new SetNoteFilterAssignmentsPatternEffect(""),
            Fields(filter.Id, "stage.CutoffHz = @\"Broken\""), song: song),
            Throws.ArgumentException);
        Assert.That(song.AudioRevision, Is.EqualTo(revision));
    }
}
