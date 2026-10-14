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
    public void RewriterUpsertsExistingRowsInPlaceAndPreservesEveryOtherLine()
    {
        FilterInstance filter = Create();
        string original = """
            # stage2: existing annotation remains
              STAGE2.cutoffhz  =  300
            # unrelated keeps its exact text
            stage20.CutoffHz = 1200
            # stage2: second annotation also remains
            STAGE2.Q = 1.4
            # stage20: untouched
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
            filter, original, 2, changes, "SHOULD NOT APPEAR");
        Assert.That(rewritten, Is.EqualTo("""
            # stage2: existing annotation remains
              STAGE2.cutoffhz  =  2500
            # unrelated keeps its exact text
            stage20.CutoffHz = 1200
            # stage2: second annotation also remains
            STAGE2.Q = 0.70710678118654757
            # stage20: untouched
            stage20.Q = 0.9
            """));
        Assert.That(FilterAssignmentText.RewriteStage(
            filter, rewritten, 2, changes, "new annotation"),
            Is.EqualTo(rewritten));
        Assert.That(FilterAssignmentText.ParseAndValidate(filter, rewritten)
            .Select(x => x.StageId), Is.EqualTo(new[] { 2, 20 }));
    }

    [Test]
    public void MissingParametersAppendBesideStageRowsWithoutAddingHeader()
    {
        FilterInstance filter = Create();
        string original = "stage2.CutoffHz = 250\n# preserve\nstage20.CutoffHz = 500";
        string rewritten = FilterAssignmentText.RewriteStage(
            filter, original, 2, [
                new("CutoffHz", FilterParameterValue.FromNumber(700)),
                new("Q", FilterParameterValue.FromNumber(1.1)),
            ], "NO HEADER");
        Assert.That(rewritten, Is.EqualTo(
            "stage2.CutoffHz = 700\nstage2.Q = 1.1000000000000001\n# preserve\nstage20.CutoffHz = 500"));
        Assert.That(rewritten, Does.Not.Contain("# stage2:"));
        Assert.That(FilterAssignmentText.RewriteStage(filter, rewritten, 2, [
            new("CutoffHz", FilterParameterValue.FromNumber(700)),
            new("Q", FilterParameterValue.FromNumber(1.1)),
        ], "NO HEADER"), Is.EqualTo(rewritten));
    }

    [Test]
    public void NewStageGetsOneHeadingButExistingHeadingIsReused()
    {
        FilterInstance filter = Create();
        string other = "# stage20: existing\nstage20.CutoffHz = 250\n";
        string inserted = FilterAssignmentText.RewriteStage(
            filter, other, 2, [
                new("CutoffHz", FilterParameterValue.FromNumber(1000)),
            ], "New stage");
        Assert.That(inserted, Does.Contain("# stage2: New stage"));
        Assert.That(inserted, Does.Contain("stage2.CutoffHz = 1000"));
        Assert.That(inserted, Does.Contain("stage20.CutoffHz = 250"));
        Assert.That(FilterAssignmentText.RewriteStage(filter, inserted, 2, [
            new("CutoffHz", FilterParameterValue.FromNumber(1000)),
        ], "replaced"), Is.EqualTo(inserted));

        string headingOnly = "# stage2: already defined\n# keep this too\n";
        string reused = FilterAssignmentText.RewriteStage(filter, headingOnly, 2, [
            new("CutoffHz", FilterParameterValue.FromNumber(1000)),
        ], "must not duplicate");
        Assert.That(reused, Does.Contain("# stage2: already defined"));
        Assert.That(reused, Does.Not.Contain("must not duplicate"));
        Assert.That(reused.Split("# stage2:").Length, Is.EqualTo(2));
    }

    [Test]
    public void PreviewReadsValidNumericAndChoiceFieldsDespiteMalformedLines()
    {
        FilterInstance filter = Create();
        string source = """
            # stage2: already existing
            stage2.CutoffHz = 400
            unrelated text ignored by preview
            stage20.CutoffHz = 550
            stage2.Q = broken
            stage2.Q = 0.9
            stage2.CutoffHz = 700
            stage2.invalid = 3
            """;
        var preview = FilterAssignmentText.PreviewStage(filter, source, 2);
        Assert.That(preview["CutoffHz"].NumericValue, Is.EqualTo(700));
        Assert.That(preview["Q"].NumericValue, Is.EqualTo(0.9));
        Assert.That(preview.ContainsKey("invalid"), Is.False);
        Assert.That(filter.GetStageParameters(2)
            .GetParameter("CutoffHz").NumericValue, Is.Not.EqualTo(700));
        Assert.That(() => FilterAssignmentText.ParseAndValidate(filter, source),
            Throws.ArgumentException, "strict Save still rejects invalid text");

        FilterDefinition definition = new((ObjectId)54U, "Choice", [
            new FilterStageDefinition(1, "comb", FilterKind.LadderSaturated),
        ]);
        var choices = FilterAssignmentText.PreviewStage(new FilterInstance(definition),
            "comb.Oversampling = unsupported\ncomb.Drive = 5\ncomb.Oversampling = 4x", 1);
        Assert.That(choices["Drive"].NumericValue, Is.EqualTo(5));
        Assert.That(choices["Oversampling"].ChoiceValue, Is.EqualTo("4x"));
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

    [Test]
    public void RuntimeRetainsValidRowsWhileStrictEditorRejectsTheSameText()
    {
        FilterInstance filter = Create();
        string source = """
            # only one malformed row is ignored, not its valid siblings
            stage2.CutoffHz = 1600
            stage20.Q = broken
            unknown.CutoffHz = 500
            stage2.Q = 0.8
            stage2.CutoffHz = nope
            stage2.CutoffHz = 1700
            stage2.Nonexistent = 11
            stage20.Q = 1.1
            stage20.Q = NaN
            stage2.Q = 0.9
            invalid line
            """;
        FilterInstance result = FilterAssignmentText.ApplyValidToClone(filter, source);
        Assert.Multiple(() =>
        {
            Assert.That(result.GetStageParameters(2)
                .GetParameter("CutoffHz").NumericValue, Is.EqualTo(1700));
            Assert.That(result.GetStageParameters(2)
                .GetParameter("Q").NumericValue, Is.EqualTo(0.9));
            Assert.That(result.GetStageParameters(20)
                .GetParameter("Q").NumericValue, Is.EqualTo(1.1));
            Assert.That(filter.GetStageParameters(2)
                .GetParameter("CutoffHz").NumericValue, Is.Not.EqualTo(1700));
            Assert.That(() => FilterAssignmentText.ParseAndValidate(filter, source),
                Throws.ArgumentException,
                "the editor must continue diagnosing incorrect authored rows");
        });
        Assert.That(FilterAssignmentText.ApplyValidToClone(filter,
            "unknown.nope=2\n# okay\nstage2.Q = nonsense").Revision,
            Is.EqualTo(filter.Revision), "all-invalid text must be a no-op");
    }

    [Test]
    public void RuntimeAcceptsCoupledFirPairAndDropsOnlyConflictingLaterCutoff()
    {
        FilterDefinition definition = new((ObjectId)834U, "Band", [
            new FilterStageDefinition(1, "band", FilterKind.FirBandPass),
            new FilterStageDefinition(2, "other", FilterKind.OnePole),
        ]);
        FilterInstance original = new(definition);
        FilterInstance both = FilterAssignmentText.ApplyValidToClone(original, """
            band.LowCutoffHz = 5000
            band.HighCutoffHz = 6000
            band.TapCount = 129
            other.CutoffHz = 900
            """);
        Assert.Multiple(() =>
        {
            Assert.That(both.GetStageParameters(1).GetParameter("LowCutoffHz")
                .NumericValue, Is.EqualTo(5000));
            Assert.That(both.GetStageParameters(1).GetParameter("HighCutoffHz")
                .NumericValue, Is.EqualTo(6000),
                "both values must be validated as one atomic coupled pair");
            Assert.That(both.GetStageParameters(1).GetParameter("TapCount")
                .NumericValue, Is.EqualTo(129));
        });

        FilterInstance conflict = FilterAssignmentText.ApplyValidToClone(original, """
            band.LowCutoffHz = 2000
            band.HighCutoffHz = 1500
            band.TapCount = 129
            band.TapCount = 128
            other.CutoffHz = 900
            """);
        Assert.Multiple(() =>
        {
            Assert.That(conflict.GetStageParameters(1).GetParameter("LowCutoffHz")
                .NumericValue, Is.EqualTo(2000));
            Assert.That(conflict.GetStageParameters(1).GetParameter("HighCutoffHz")
                .NumericValue, Is.EqualTo(4000),
                "later conflicting cutoff is dropped, earlier valid one retained");
            Assert.That(conflict.GetStageParameters(1).GetParameter("TapCount")
                .NumericValue, Is.EqualTo(129),
                "an invalid duplicate does not erase a valid earlier value");
            Assert.That(conflict.GetStageParameters(2).GetParameter("CutoffHz")
                .NumericValue, Is.EqualTo(900));
        });
    }

    [Test]
    public void RuntimePreflightDropsOnlyAnUnsupportedFieldWithoutCommittingTrials()
    {
        FilterInstance original = Create();
        int probes = 0;
        FilterInstance accepted = FilterAssignmentText.ApplyValidToClone(original,
            "stage2.CutoffHz = 7000\nstage2.Q = 0.85\nstage20.CutoffHz = 600",
            proposed =>
            {
                probes++;
                return proposed.GetStageParameters(2)
                    .GetParameter("CutoffHz").NumericValue <= 5000;
            });
        Assert.Multiple(() =>
        {
            Assert.That(probes, Is.GreaterThan(1));
            Assert.That(accepted.GetStageParameters(2)
                .GetParameter("CutoffHz").NumericValue, Is.EqualTo(1000));
            Assert.That(accepted.GetStageParameters(2)
                .GetParameter("Q").NumericValue, Is.EqualTo(0.85));
            Assert.That(accepted.GetStageParameters(20)
                .GetParameter("CutoffHz").NumericValue, Is.EqualTo(600));
            Assert.That(original.Revision, Is.Zero,
                "preflight probes only evaluate clones of the original Filter");
        });
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
