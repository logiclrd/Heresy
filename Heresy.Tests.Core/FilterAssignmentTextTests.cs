using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterAssignmentTextTests
{
    [Test]
    public void StageSpecificRewriterRemovesAllOldAnnotationsAndAssignments()
    {
        FilterInstance filter = Create();
        string original = """
            # stage2: obsolete annotation
            stage2.CutoffHz = 300
            # unrelated keeps its exact text
            stage20.CutoffHz = 1200
            # stage2: second obsolete block
            STAGE2.Q = 1.4
            # stage20: must survive verbatim
            stage20.Q = 0.9
            """;
        var changes = new[]
        {
            new KeyValuePair<string, FilterParameterValue>(
                "CutoffHz", FilterParameterValue.FromNumber(2500)),
            new KeyValuePair<string, FilterParameterValue>(
                "Q", FilterParameterValue.FromNumber(0.7071067811865476)),
        };
        string rewritten = FilterAssignmentText.RewriteStage(
            filter, original, 2, changes, "Low-pass filter at 2500 Hz");
        Assert.That(rewritten, Does.Contain("# stage2: Low-pass filter at 2500 Hz"));
        Assert.That(rewritten, Does.Contain("stage2.CutoffHz = 2500"));
        Assert.That(rewritten, Does.Not.Contain("obsolete"));
        Assert.That(rewritten, Does.Not.Contain("STAGE2.Q = 1.4"));
        Assert.That(rewritten, Does.Contain("stage20.CutoffHz = 1200"));
        Assert.That(rewritten, Does.Contain("# stage20: must survive verbatim"));
        Assert.That(rewritten, Does.Contain("# unrelated keeps its exact text"));
        Assert.That(FilterAssignmentText.RewriteStage(filter,
            rewritten, 2, changes, "Low-pass filter at 2500 Hz"),
            Is.EqualTo(rewritten), "repeat wizard rewrites cannot accumulate annotations");
    }

    [Test]
    public void RewriterAppendsAbsentStageAndPreservesOtherStageWithSimilarPrefix()
    {
        FilterInstance filter = Create();
        string source = "# stage20: existing\nstage20.CutoffHz = 250\n";
        string rewritten = FilterAssignmentText.RewriteStage(
            filter, source, 2, [
                new("CutoffHz", FilterParameterValue.FromNumber(1000)),
            ], "New stage");
        Assert.That(rewritten, Does.StartWith("# stage20: existing"));
        Assert.That(rewritten, Does.Contain("stage20.CutoffHz = 250"));
        Assert.That(rewritten, Does.Contain("# stage2: New stage"));
        Assert.That(FilterAssignmentText.RewriteStage(filter, rewritten, 2, [
                new("CutoffHz", FilterParameterValue.FromNumber(1000)),
            ], "New stage"), Is.EqualTo(rewritten));
    }

    [Test]
    public void ParserIgnoresCommentsResolvesCanonicalNamesAndUsesInvariantNumbers()
    {
        FilterInstance filter = Create();
        var groups = FilterAssignmentText.ParseAndValidate(filter, """
            # Default is intentionally ignored

              STAGE2.cutoffhz  =  2500
            stage2.q = 0.7071067811865476
            stage20.CutoffHz = 1500
            """);
        Assert.That(groups.Select(x => x.StageId), Is.EqualTo(new[] { 2, 20 }));
        Assert.That(groups[0].Assignments[0].Key, Is.EqualTo("CutoffHz"));
        Assert.That(groups[0].Assignments[0].Value.NumericValue, Is.EqualTo(2500));
        Assert.That(groups[0].Assignments[1].Key, Is.EqualTo("Q"));
        Assert.That(groups[1].StageId, Is.EqualTo(20));
        double old = filter.GetStageParameters(2).GetParameter("CutoffHz").NumericValue;
        FilterAssignmentText.ApplyToClone(filter, "stage2.CutoffHz = 2500");
        Assert.That(filter.GetStageParameters(2).GetParameter("CutoffHz").NumericValue,
            Is.EqualTo(old), "parse/preview cannot alter the original use");
    }

    [TestCase("stage2.CutoffHz = 1200\nSTAGE2.cutoffhz = 1500", "Duplicate")]
    [TestCase("stage200.CutoffHz = 900", "Unknown stage")]
    [TestCase("stage2.InvalidParameter = 9", "Unknown parameter")]
    [TestCase("stage2.CutoffHz = 1,25", "invariant-culture")]
    [TestCase("stage2.CutoffHz = NaN", "invalid")]
    [TestCase("stage2.CutoffHz = 1200\ninvalid line", "Line 2")]
    [TestCase("stage2.CutoffHz = 1200 = 1250", "Line 1")]
    public void InvalidTextFailsWithoutMutatingEarlierAssignments(
        string source, string message)
    {
        FilterInstance filter = Create();
        var baseline = filter.GetStageParameters(2)
            .GetParameter("CutoffHz");
        Assert.That(() => FilterAssignmentText.ApplyToClone(filter, source),
            Throws.ArgumentException.With.Message.Contains(message)
                .IgnoreCase);
        Assert.That(filter.GetStageParameters(2)
            .GetParameter("CutoffHz"), Is.EqualTo(baseline));
    }

    [Test]
    public void ChoiceFieldsUseSelectedStageVocabularyAndRejectInvalidValues()
    {
        FilterDefinition definition = new((ObjectId)54U, "Choice", [
            new FilterStageDefinition(1, "comb", FilterKind.LadderSaturated),
        ]);
        FilterInstance filter = new(definition);
        var updated = FilterAssignmentText.ApplyToClone(filter,
            "# oversampling\ncomb.Oversampling = 4x\ncomb.Drive = 5");
        Assert.That(updated.GetStageParameters(1)
            .GetParameter("Oversampling").ChoiceValue, Is.EqualTo("4x"));
        Assert.That(() => FilterAssignmentText.ApplyToClone(filter,
            "comb.Oversampling = unsupported"),
            Throws.ArgumentException);
        Assert.That(filter.GetStageParameters(1).GetParameter("Oversampling")
            .ChoiceValue, Is.Not.EqualTo("4x"));
    }

    [Test]
    public void InterdependentFirBandEdgesValidateAcrossSameStageBeforeCommit()
    {
        FilterDefinition definition = new((ObjectId)55U, "FIR", [
            new FilterStageDefinition(1, "band", FilterKind.FirBandPass),
            new FilterStageDefinition(2, "one", FilterKind.OnePole),
        ]);
        FilterInstance filter = new(definition);
        var original = filter.GetStageParameters(1).GetParameter("LowCutoffHz");
        Assert.That(() => FilterAssignmentText.ApplyToClone(filter,
            "one.CutoffHz = 300\nband.LowCutoffHz = 9500\nband.HighCutoffHz = 200"),
            Throws.ArgumentException);
        Assert.That(filter.GetStageParameters(1).GetParameter("LowCutoffHz"),
            Is.EqualTo(original));
    }

    [Test]
    public void PersistenceRoundTripsNativeStageTextAndEditorOnlyHint()
    {
        var effect = new SetChannelFilterAssignmentsPatternEffect(
            "# stage2: set\nstage2.CutoffHz = 2500", (ObjectId)17U);
        string json = SongDocumentJson.SerializePatternEffects([effect]);
        PatternEffect decoded = SongDocumentJson.DeserializePatternEffects(json).Single();
        Assert.That(decoded, Is.EqualTo(effect));
    }

    private static FilterInstance Create()
    {
        FilterDefinition definition = new((ObjectId)53U, "Test", [
            new FilterStageDefinition(2, "stage2", FilterKind.BiquadLowPass),
            new FilterStageDefinition(20, "stage20", FilterKind.BiquadLowPass),
        ]);
        return new FilterInstance(definition);
    }
}
