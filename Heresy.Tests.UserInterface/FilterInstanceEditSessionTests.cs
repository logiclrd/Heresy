using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterInstanceEditSessionTests
{
	[Test]
	public void ApplyingOneUsePublishesIndependentCopyWithoutMutatingAnother()
	{
		FilterDefinition definition = new((ObjectId)2U, "Shared");
		FilterInstance first = new(definition);
		FilterInstance second = new(definition);
		int publications = 0;
		FilterInstanceEditSession edit = new(
			() => first,
			published => { first = published; publications++; });
		FilterInstance original = first;
		Assert.That(edit.ApplyStage(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(500)),
		}), Is.True);
		Assert.That(publications, Is.EqualTo(1));
		Assert.That(first, Is.Not.SameAs(original));
		Assert.That(original.GetOverrides(1), Is.Empty);
		Assert.That(second.GetOverrides(1), Is.Empty);
		Assert.That(first.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(500));
		Assert.That(edit.ApplyStage(1, new[] {
			Pair("cutoffhz", FilterParameterValue.FromNumber(500)),
		}), Is.False);
		Assert.That(publications, Is.EqualTo(1));
	}

	[Test]
	public void BothCopyOnWriteAndInPlaceConcurrentEditsRejectStaleSession()
	{
		FilterDefinition definition = new((ObjectId)3U, "Shared");
		FilterInstance current = new(definition);
		FilterInstanceEditSession a = new(() => current, x => current = x);
		FilterInstanceEditSession b = new(() => current, x => current = x);
		a.ApplyStage(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(2800)),
		});
		Assert.That(b.IsStale, Is.True);
		Assert.Throws<InvalidOperationException>(() => b.ApplyStage(1, new[] {
			Pair("Mode", FilterParameterValue.FromChoice("HighPass")),
		}));
		Assert.That(current.GetOverrides(1).ContainsKey("Mode"), Is.False);
		b.Reload();
		Assert.That(b.ApplyStage(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(2800)),
			Pair("Mode", FilterParameterValue.FromChoice("HighPass")),
		}), Is.True);
		Assert.That(current.GetOverrides(1).Count, Is.EqualTo(2));

		FilterInstanceEditSession c = new(() => current, x => current = x);
		current.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3200));
		Assert.That(c.IsStale, Is.True);
		Assert.Throws<InvalidOperationException>(() => c.ApplyStage(1,
			Array.Empty<KeyValuePair<string, FilterParameterValue>>()));
	}

	[Test]
	public void SharedDefinitionChangeInvalidatesDraftWithoutPublishing()
	{
		FilterDefinition definition = new((ObjectId)4U, "Shared");
		FilterInstance instance = new(definition);
		int calls = 0;
		FilterInstanceEditSession session = new(
			() => instance, x => { instance = x; calls++; });
		definition.SetStageDefaults(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(900)),
		});
		Assert.That(session.IsStale, Is.True);
		Assert.Throws<InvalidOperationException>(() => session.ApplyStage(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(500)),
		}));
		Assert.That(calls, Is.Zero);
		session.Reload();
		Assert.That(session.IsStale, Is.False);
		Assert.That(session.ApplyStage(1, new[] {
			Pair("CutoffHz", FilterParameterValue.FromNumber(500)),
		}), Is.True);
		Assert.That(calls, Is.EqualTo(1));
	}

	[Test]
	public void InvalidBatchAndFailedPublicationDoNotChangeSessionBaseline()
	{
		FilterDefinition definition = new((ObjectId)5U, "Band", new[] {
			new FilterStageDefinition(7, "band", FilterKind.FirBandPass),
		});
		FilterInstance current = new(definition);
		bool throwOnCommit = true;
		int calls = 0;
		FilterInstanceEditSession session = new(
			() => current,
			proposed =>
			{
				calls++;
				if (throwOnCommit)
					throw new InvalidOperationException("Owner rejected update.");
				current = proposed;
			});

		Assert.Throws<ArgumentException>(() =>
			session.ApplyStage(7, new[] {
				Pair("LowCutoffHz", FilterParameterValue.FromNumber(6000)),
				Pair("HighCutoffHz", FilterParameterValue.FromNumber(1000)),
			}));
		Assert.That(calls, Is.Zero);
		Assert.That(session.IsStale, Is.False);
		Assert.That(current.GetOverrides(7), Is.Empty);

		Assert.Throws<InvalidOperationException>(() =>
			session.ApplyStage(7, new[] {
				Pair("TapCount", FilterParameterValue.FromNumber(255)),
			}));
		Assert.That(calls, Is.EqualTo(1));
		Assert.That(current.GetOverrides(7), Is.Empty);
		throwOnCommit = false;
		Assert.That(session.ApplyStage(7, new[] {
			Pair("TapCount", FilterParameterValue.FromNumber(255)),
		}), Is.True);
		Assert.That(calls, Is.EqualTo(2));
		Assert.That(current.GetOverrides(7)["TapCount"].NumericValue,
			Is.EqualTo(255));
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string key, FilterParameterValue value) => new(key, value);
}
