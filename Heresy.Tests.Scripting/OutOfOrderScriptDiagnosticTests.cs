using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class OutOfOrderScriptDiagnosticTests
{
	[Test]
	public void EagerAndStreamingScriptBothPostNonfatalOutOfOrderDiagnostics()
	{
		ScriptPatternDefinition script = new((ObjectId)1U, "Diagnostics")
		{
			RowCount = 6,
			Source = "Cut(4, 0); Off(1, 0); Cut(4, 0);",
		};

		var eager = ScriptCompiler.CompilePattern(script);
		eager.Success.Should().BeTrue();
		SequencingContext eagerContext = new();
		NoteScheduleBuilder eagerEvents = new();
		eager.Program!.GenerateRawNotes(eagerContext, eagerEvents, out _);
		eagerEvents.Freeze().Select(e => e.Offset.RowOffset).Should().Equal(4, 4);
		eagerContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(1);
		eagerContext.Diagnostics.Drain().Single().Row.Should().Be(1);

		var streaming = ScriptCompiler.CompileIncrementalPattern(script);
		streaming.Success.Should().BeTrue();
		SequencingContext streamingContext = new();
		var notes = streaming.Program!
			.EnumerateRawSteps(streamingContext)
			.OfType<RawPatternStep.Emit>().ToArray();
		notes.Select(x => x.Row).Should().Equal(4, 4);
		streamingContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(1);
		streamingContext.Diagnostics.Drain().Single().Row.Should().Be(1);
	}
}
