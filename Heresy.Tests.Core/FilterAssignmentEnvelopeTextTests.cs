using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterAssignmentEnvelopeTextTests
{
    private static FilterInstance Filter() => new(new FilterDefinition(
        (ObjectId)300U, "Test",
        [
            new FilterStageDefinition(2, "low", FilterKind.BiquadLowPass),
            new FilterStageDefinition(3, "ladder", FilterKind.LadderSaturated),
            new FilterStageDefinition(4, "fir", FilterKind.FirLowPass),
        ]));

    private static SongDocument Song(params string[] names)
    {
        SongDocument song = new();
        for (int i = 0; i < names.Length; i++)
        {
            EnvelopeDefinition envelope = (i % 2 == 0)
                ? new NodeEnvelopeDefinition(song.AllocateObjectId(), names[i])
                : new ScriptEnvelopeDefinition(song.AllocateObjectId(), names[i]);
            song.Add(envelope);
        }
        return song;
    }

    [Test]
    public void MixedTextKeepsNumericBatchesAndResolvesNodeAndScriptEnvelopeIds()
    {
        SongDocument song = Song("Cutoff Sweep", "Resonance Sweep");
        FilterInstance selected = Filter();
        var before = selected.Revision;
        var captured = FilterAssignmentEnvelopeNames.Capture(song);
        string text = """
            # original numeric syntax remains fully supported
            low.CutoffHz = 900
            low.Q = @"resonance sweep"
            fir.CutoffHz = @"Cutoff Sweep"
            ladder.Drive = 4
            """;
        var result = FilterAssignmentEnvelopeText.ParseAndValidate(
            selected, text, captured);
        Assert.Multiple(() =>
        {
            Assert.That(result.Count, Is.EqualTo(4));
            Assert.That(result.NumericBatches.Select(batch => batch.StageId),
                Is.EquivalentTo(new[] { 2, 3 }));
            Assert.That(result.NumericBatches[0].Assignments[0].Key,
                Is.EqualTo("CutoffHz"));
            Assert.That(result.NumericBatches[0].Assignments[0].Value.NumericValue,
                Is.EqualTo(900));
            Assert.That(result.EnvelopeBindings.Select(binding => binding.StageId),
                Is.EqualTo(new[] { 2, 4 }));
            Assert.That(result.EnvelopeBindings[0].ParameterName, Is.EqualTo("Q"));
            Assert.That(result.EnvelopeBindings[0].EnvelopeId,
                Is.EqualTo(song.Objects.Values.OfType<ScriptEnvelopeDefinition>().Single().Id));
            Assert.That(result.EnvelopeBindings[1].EnvelopeId,
                Is.EqualTo(song.Objects.Values.OfType<NodeEnvelopeDefinition>().Single().Id));
            Assert.That(result.EnvelopeBindings[0].LineNumber, Is.EqualTo(3));
            Assert.That(selected.Revision, Is.EqualTo(before),
                "strict validation is a read-only analysis");
        });
    }

    [Test]
    public void QuotedNamesSupportEscapedQuoteBackslashAndLiteralEquals()
    {
        SongDocument song = Song("A \"quoted\" = name\\path");
        const string escaped = "low.CutoffHz = @\"A \\\"quoted\\\" = name\\\\path\"";
        var binding = FilterAssignmentEnvelopeText.ParseAndValidate(
            Filter(), escaped, FilterAssignmentEnvelopeNames.Capture(song))
            .EnvelopeBindings.Single();
        Assert.That(binding.EnvelopeId,
            Is.EqualTo(song.Objects.Values.OfType<NodeEnvelopeDefinition>().Single().Id));
    }

    [TestCase("low.Q = @cutoff", "quoted")]
    [TestCase("low.Q = @\"unclosed", "Unterminated")]
    [TestCase("low.Q = @\"x\" junk", "Unexpected")]
    [TestCase("low.Q = @\"\"", "empty")]
    [TestCase("low.Q = @\"x\\q\"", "escape")]
    [TestCase("low.BadProperty = @\"x\"", "Unknown parameter")]
    [TestCase("fir.TapCount = @\"x\"", "cannot use an Envelope")]
    [TestCase("ladder.Oversampling = @\"x\"", "cannot use an Envelope")]
    [TestCase("missing.Q = @\"x\"", "Unknown stage")]
    [TestCase("low.Q = @\"unknown\"", "does not exist")]
    public void MalformedOrUnsupportedReferenceHasAuthoringDiagnostic(
        string text, string message)
        => Assert.That(() => FilterAssignmentEnvelopeText.ParseAndValidate(
                Filter(), text, FilterAssignmentEnvelopeNames.Capture(Song("x"))),
            Throws.ArgumentException.With.Message.Contains(message).IgnoreCase);

    [Test]
    public void CaseInsensitiveAmbiguityAndMixedDuplicatePathsFailWithoutBinding()
    {
        SongDocument duplicates = Song("Speed", "SPEED");
        Assert.That(() => FilterAssignmentEnvelopeText.ParseAndValidate(
                Filter(), "low.Q = @\"speed\"",
                FilterAssignmentEnvelopeNames.Capture(duplicates)),
            Throws.ArgumentException.With.Message.Contains("ambiguous"));
        SongDocument song = Song("Sweep");
        foreach (string text in new[]
            {
                "low.Q = 0.8\nLOW.q = @\"Sweep\"",
                "low.Q = @\"Sweep\"\nlow.Q = 0.8",
                "low.Q = @\"Sweep\"\nLOW.Q = @\"Sweep\"",
            })
        {
            Assert.That(() => FilterAssignmentEnvelopeText.ParseAndValidate(
                    Filter(), text, FilterAssignmentEnvelopeNames.Capture(song)),
                Throws.ArgumentException.With.Message.Contains("Duplicate"), text);
        }
    }

    [TestCase("low.Q = @\"x\" ThEn OfF", FilterEnvelopeCompletionAction.Off)]
    [TestCase("low.Q = @\"x\" THEN CUT", FilterEnvelopeCompletionAction.Cut)]
    [TestCase("low.Q = @\"x\"  then   cut  ", FilterEnvelopeCompletionAction.Cut)]
    [TestCase("low.Q = @\"x\"", FilterEnvelopeCompletionAction.None)]
    public void OptionalCompletionSuffixProducesTypedAction(
        string source, FilterEnvelopeCompletionAction expected)
    {
        var selected = Filter();
        var names = FilterAssignmentEnvelopeNames.Capture(Song("x"));
        var binding = FilterAssignmentEnvelopeText.ParseAndValidate(selected, source, names)
            .EnvelopeBindings.Single();
        var action = FilterAssignmentEnvelopeText.ResolveRuntimeActions(
            selected, source, names).Single();
        var preview = FilterAssignmentEnvelopeText.ResolveValidBindings(
            selected, source, names).Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.CompletionAction, Is.EqualTo(expected));
            Assert.That(action.CompletionAction, Is.EqualTo(expected));
            Assert.That(preview.CompletionAction, Is.EqualTo(expected));
        });
    }

    [TestCase("low.Q = @\"x\"THEN CUT")]
    [TestCase("low.Q = @\"x\" THEN")]
    [TestCase("low.Q = @\"x\" THEN STOP")]
    [TestCase("low.Q = @\"x\" THEN OFF THEN CUT")]
    [TestCase("low.Q = @\"x\" CUT")]
    public void InvalidCompletionSuffixIsRejectedButDoesNotClobberRuntimeSibling(
        string malformed)
    {
        var selected = Filter();
        var names = FilterAssignmentEnvelopeNames.Capture(Song("x"));
        Assert.That(() => FilterAssignmentEnvelopeText.ParseAndValidate(
            selected, malformed, names),
            Throws.ArgumentException.With.Message.Contains("Unexpected"));
        var actions = FilterAssignmentEnvelopeText.ResolveRuntimeActions(
            selected, "low.Q = @\"x\" THEN OFF\n" + malformed, names);
        Assert.That(actions.Single().CompletionAction,
            Is.EqualTo(FilterEnvelopeCompletionAction.Off));
    }

    [Test]
    public void LaterNumericAssignmentDetachesAndLaterEnvelopeOverridesCompletionAction()
    {
        var selected = Filter();
        var names = FilterAssignmentEnvelopeNames.Capture(Song("x"));
        var actions = FilterAssignmentEnvelopeText.ResolveRuntimeActions(selected,
            "low.Q = @\"x\" THEN OFF\nlow.Q = 0.7\nlow.Q = @\"x\" THEN CUT",
            names);
        Assert.That(actions, Has.Count.EqualTo(1));
        Assert.That(actions[0].CompletionAction,
            Is.EqualTo(FilterEnvelopeCompletionAction.Cut));
        Assert.That(actions[0].LineNumber, Is.EqualTo(3));
        var detached = FilterAssignmentEnvelopeText.ResolveRuntimeActions(
            selected, "low.Q = @\"x\" THEN CUT\nlow.Q = 0.6", names);
        Assert.That(detached.Single().EnvelopeId, Is.Null);
        Assert.That(detached.Single().CompletionAction,
            Is.EqualTo(FilterEnvelopeCompletionAction.None));
    }

    [Test]
    public void QuotedNameMayContainThenWordsWithoutBecomingAnAction()
    {
        var names = FilterAssignmentEnvelopeNames.Capture(Song("Attack THEN OFF"));
        var binding = FilterAssignmentEnvelopeText.ParseAndValidate(
            Filter(), "low.Q = @\"Attack THEN OFF\" THEN CUT", names)
            .EnvelopeBindings.Single();
        Assert.That(binding.CompletionAction, Is.EqualTo(FilterEnvelopeCompletionAction.Cut));
    }

    [Test]
    public void CapturedNamesDoNotShiftWhenSongIsRenamedOrNewAmbiguousObjectAdded()
    {
        SongDocument song = Song("Original");
        ObjectId expected = song.Objects.Values.OfType<EnvelopeDefinition>().Single().Id;
        var frozen = FilterAssignmentEnvelopeNames.Capture(song);
        song.Objects.Values.OfType<EnvelopeDefinition>().Single().Name = "Renamed";
        song.Add(new ScriptEnvelopeDefinition(song.AllocateObjectId(), "Original"));
        Assert.That(frozen.Resolve("Original"), Is.EqualTo(expected));
        Assert.That(FilterAssignmentEnvelopeNames.Capture(song).Resolve("Original"),
            Is.Not.EqualTo(expected));
        Assert.That(() => frozen.Resolve("Renamed"),
            Throws.ArgumentException.With.Message.Contains("does not exist"));
    }

    [Test]
    public void AuthoredEnvelopeReferenceSurvivesBothNativeEffectsAndJsonWithoutRewriting()
    {
        const string text = "# editor preserves the reference\nlow.Q = @\"Envelope One\" THEN CUT";
        var a = new SetNoteFilterAssignmentsPatternEffect(text, (ObjectId)301U);
        var b = new SetChannelFilterAssignmentsPatternEffect(text, (ObjectId)301U);
        string serialized = SongDocumentJson.SerializePatternEffects([a, b]);
        var restored = SongDocumentJson.DeserializePatternEffects(serialized);
        Assert.That(restored, Is.EquivalentTo(new PatternEffect[] { a, b }));
        Assert.That(((SetNoteFilterAssignmentsPatternEffect)restored[0])
            .AssignmentText, Is.EqualTo(text));
    }
}
