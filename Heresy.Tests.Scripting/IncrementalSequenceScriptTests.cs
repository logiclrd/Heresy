using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class IncrementalSequenceScriptTests
{
	[Test]
	public void PlayYieldsBeforeTheNextScriptStatementRuns()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Lazy sequence")
		{
			Source = "Play(_O(17), 2); Play(_O(18), -1);",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		using var enumerator = compiled.Program!
			.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		enumerator.MoveNext().Should().BeTrue();
		enumerator.Current.Should().BeOfType<RawSequenceStep.Play>()
			.Which.Entry.Should().Be(new SequenceEntry((ObjectId)17U, 2));
		Action resume = () => enumerator.MoveNext();
		resume.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void SilentInfiniteScriptLoopYieldsCpuOnlyCheckpoints()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Silent sequence")
		{
			Source = "while (true) { }",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		using var enumerator = compiled.Program!
			.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		for (int i = 0; i < 3; i++)
		{
			enumerator.MoveNext().Should().BeTrue();
			enumerator.Current.Should().BeOfType<RawSequenceStep.Cooperate>();
		}
	}

	[Test]
	public void RandomAndLocalStateSurviveSuspensionAndAreInvocationLocal()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Random sequence")
		{
			Source = """
				int i = 0;
				while (i++ < 3)
				{
					if (Random() < 0.5)
						Play(_O(17), i);
					else
						Play(_O(18), i);
				}
				""",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		static SequenceEntry[] Extract(IIncrementalRawSequenceEntryGenerator generator)
			=> generator.EnumerateRawSteps(new SequencingContext(
				random: new DeterministicRandom(123456)))
				.OfType<RawSequenceStep.Play>().Select(x => x.Entry).ToArray();

		SequenceEntry[] first = Extract(compiled.Program!);
		SequenceEntry[] second = Extract(compiled.Program!);
		first.Should().Equal(second);
		first.Select(e => e.StartRow).Should().Equal(1, 2, 3);
		first.Should().HaveCount(3);
	}

	[Test]
	public void NestedPlayExpressionIsRejectedByStreamingOnly()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Unsupported")
		{
			Source = "for (int i = 0; i < 2; Play(_O(17))) { i++; }",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeFalse();
		compiled.Diagnostics.Should().Contain(d => d.Code == "HRS2001");
	}

	[Test]
	public void RestrictedFrameworkAccessStaysRejected()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Sandbox")
		{
			Source = "System.IO.File.ReadAllText(\"forbidden\");",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeFalse();
		compiled.Diagnostics.Should().Contain(d => d.Code == "HRS2001");
	}
}
