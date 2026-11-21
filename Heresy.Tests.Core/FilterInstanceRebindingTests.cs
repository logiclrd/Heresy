using System;
using System.Collections;
using System.Linq;
using System.Reflection;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterInstanceRebindingTests
{
    [Test]
    public void RebindExactDefinitionPreservesDormantOverridesAndOriginalRevision()
    {
        FilterDefinition original = new(new ObjectId(1), "Original");
        FilterInstance configured = new(original);
        configured.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(2300));
        long revision = configured.Revision;
        // The override is dormant after the stage kind changes, but must
        // remain part of the full authored FilterInstance clone state.
        original.ChangeStageKind(1, FilterKind.FirLowPass);
        Assert.That(configured.GetOverrides(1), Is.Empty);

        FilterDefinition duplicate = new(new ObjectId(2), "Duplicate",
            original.Stages.Select(stage => new FilterStageDefinition(
                stage.StageId, stage.Name, stage.Kind, stage.DefaultParameters)));
        duplicate.RestoreStageIdentity(original.NextStageIdForPersistence,
            original.StageGenerationsForPersistence);

        FilterInstance rebound = configured.CloneForDefinition(duplicate);
        Assert.That(rebound, Is.Not.SameAs(configured));
        Assert.That(rebound.Definition, Is.SameAs(duplicate));
        Assert.That(configured.Definition, Is.SameAs(original));
        Assert.That(rebound.Revision, Is.EqualTo(revision));
        Assert.That(rebound.GetOverrides(1), Is.Empty);

        FieldInfo overrides = typeof(FilterInstance).GetField(
            "_overrides", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDictionary originalState = (IDictionary)overrides.GetValue(configured)!;
        IDictionary reboundState = (IDictionary)overrides.GetValue(rebound)!;
        Assert.That(reboundState.Count, Is.EqualTo(originalState.Count));
        Assert.That(reboundState.Count, Is.EqualTo(1),
            "inactive overrides must be retained rather than reconstructed from GetOverrides");
        Assert.That(reboundState, Is.Not.SameAs(originalState));
        Assert.That(reboundState[1], Is.Not.SameAs(originalState[1]));
    }

    [Test]
    public void RebindingToNonIdenticalStageIdentityIsRefused()
    {
        FilterDefinition original = new(new ObjectId(1), "Original");
        FilterInstance use = new(original);
        original.ChangeStageKind(1, FilterKind.FirLowPass);

        FilterDefinition incompatible = new(new ObjectId(2), "Different");
        Assert.That(() => use.CloneForDefinition(incompatible),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(use.Definition, Is.SameAs(original));
    }
}
