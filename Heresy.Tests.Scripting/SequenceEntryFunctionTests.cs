using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class SequenceEntryFunctionTests
{
	[Test]
	public void RoslynLookupReceivesAbsoluteCurrentAndPreviousOrderIndices()
	{
		ScriptSequenceDefinition script = new((ObjectId)1U, "Index-dependent")
		{
			Source = """
				if (absoluteIndex == 0 && sequenceIndex == 5 && previousSequenceIndex == -1)
					return Play(_O(17), 2);
				if (absoluteIndex == 1 && sequenceIndex == 2 && previousSequenceIndex == 5)
					return Play(_O(18), 1);
				if (absoluteIndex == 2 && sequenceIndex == 2 && previousSequenceIndex == 2)
					return Play(_O(19));
				return null;
				""",
		};
		var result = ScriptCompiler.CompileIncrementalSequence(script);
		result.Success.Should().BeTrue();
		ISequenceEntryProvider provider = result.Program!.Create(new SequencingContext());
		provider.GetSequenceEntry(0, 5, -1).Should().Be(
			new SequenceEntry((ObjectId)17U, 2));
		provider.GetSequenceEntry(1, 2, 5).Should().Be(
			new SequenceEntry((ObjectId)18U, 1));
		provider.GetSequenceEntry(2, 2, 2).Should().Be(
			new SequenceEntry((ObjectId)19U));
		provider.GetSequenceEntry(3, 3, 2).Should().BeNull();
	}

	[Test]
	public void ScriptCanReturnDifferentEntriesForSameSequenceIndex()
	{
		ScriptSequenceDefinition script = new((ObjectId)1U, "Alternating")
		{
			Source = """
				switch (sequenceIndex)
				{
					case 0:
						return absoluteIndex % 2 == 0
							? Play(_O(17)) : Play(_O(18));
					default:
						return null;
				}
				""",
		};
		var compiled = ScriptCompiler.CompileSequence(script, new Resolver());
		compiled.Success.Should().BeTrue();
		var source = ScriptCompiler.CompileIncrementalSequence(script);
		source.Success.Should().BeTrue();
		ISequenceEntryProvider provider = source.Program!.Create(new SequencingContext());
		provider.GetSequenceEntry(0, 0, -1)!.PatternId.Should().Be((ObjectId)17U);
		provider.GetSequenceEntry(1, 0, 0)!.PatternId.Should().Be((ObjectId)18U);
		provider.GetSequenceEntry(2, 0, 0)!.PatternId.Should().Be((ObjectId)17U);
	}

	[Test]
	public void LookupDoesNotRetainLocalVariablesBetweenCallsButRandomDoes()
	{
		ScriptSequenceDefinition script = new((ObjectId)1U, "Random")
		{
			Source = """
				int local = 1;
				if (sequenceIndex != 0)
					return null;
				if (Random() < 0.5)
					return Play(_O(17), local);
				return Play(_O(18), local);
				""",
		};
		var result = ScriptCompiler.CompileIncrementalSequence(script);
		result.Success.Should().BeTrue();
		static ObjectId[] Run(ISequenceEntrySourceFactory factory)
		{
			ISequenceEntryProvider provider = factory.Create(
				new SequencingContext(random: new DeterministicRandom(12345)));
			return Enumerable.Range(0, 6)
				.Select(i => provider.GetSequenceEntry(i, 0, i == 0 ? -1 : 0)!.PatternId)
				.ToArray();
		}
		Run(result.Program!).Should().Equal(Run(result.Program!));
	}

	private sealed class Resolver : ISequencePatternResolver
	{
		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? pattern)
		{
			pattern = null;
			return false;
		}
	}
}
