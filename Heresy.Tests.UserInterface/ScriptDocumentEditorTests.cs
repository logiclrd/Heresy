using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Scripting.Compilation;
using Heresy.Core.Patterns;
using Heresy.Core.Scripting;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptDocumentEditorTests
{
	[Test]
	public void InitialSourcesAreValidWhenSongContainsNoCompatibleObjects()
	{
		DocumentWorkspace sequences = new();
		DocumentWorkspace patterns = new();
		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(sequences, "Empty Sequence");
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(patterns, "Empty Pattern");
		sequence.Source.Should().Contain("return default;");
		pattern.Source.Should().Contain("Add a Sample");
		ScriptCompiler.CompileIncrementalSequence(sequence).Success.Should().BeTrue();
		ScriptCompiler.CompileIncrementalPattern(pattern).Success.Should().BeTrue();
	}

	[Test]
	public void NewScriptSequencePrefersActualPatternIdAndNeverRegeneratesIt()
	{
		DocumentWorkspace workspace = new();
		SongDocument document = workspace.Document;
		// A Sequence is not a valid Play() argument, even if it has a lower ID.
		DataSequenceDefinition earlier = new(document.AllocateObjectId(), "Earlier");
		document.Add(earlier);
		DataPatternDefinition chosen = new(document.AllocateObjectId(), "Actual Pattern");
		document.Add(chosen);
		ScriptSequenceDefinition generated =
			ScriptDocumentEditor.CreateScriptSequence(workspace, "Generated");
		generated.Source.Should().Contain($"Play(_O({chosen.Id.Value}))");
		generated.Source.Should().NotContain($"Play(_O({earlier.Id.Value}))");
		ScriptCompiler.CompileIncrementalSequence(generated).Success.Should().BeTrue();
		string original = generated.Source;
		document.Add(new DataPatternDefinition(document.AllocateObjectId(), "Other Pattern"));
		generated.Source.Should().Be(original);
		ScriptDocumentEditor.UpdateSource(workspace, generated, "return default;");
		generated.Source.Should().Be("return default;");
	}

	[Test]
	public void NewScriptPatternPrefersExistingSoundSourceAndDoesNotInventSemitoneHelper()
	{
		DocumentWorkspace workspace = new();
		SongDocument document = workspace.Document;
		DataSequenceDefinition source = new(document.AllocateObjectId(), "Playable Sequence");
		document.Add(source);
		ScriptPatternDefinition generated =
			ScriptDocumentEditor.CreateScriptPattern(workspace, "Pattern");
		generated.Source.Should().Contain($"_O({source.Id.Value})");
		generated.Source.Should().Contain("pitchMultiplier: 1.0");
		generated.Source.Should().NotContain("Semitone(");
		ScriptCompiler.CompileIncrementalPattern(generated).Success.Should().BeTrue();
	}

	[TestCase(".hm")]
	[TestCase(".hm.json")]
	public void InitialSourceBecomesOrdinaryPersistedText(string extension)
	{
		DocumentWorkspace workspace = new();
		SongDocument document = workspace.Document;
		DataPatternDefinition pattern = new(document.AllocateObjectId(), "Order");
		document.Add(pattern);
		ScriptSequenceDefinition generated =
			ScriptDocumentEditor.CreateScriptSequence(workspace, "Script");
		string savedSource = generated.Source;
		string path = Path.Combine(Path.GetTempPath(),
			"heresy-script-starter-" + Guid.NewGuid().ToString("N") + extension);
		try
		{
			SongDocumentStorage.Save(path, document);
			SongDocument opened = SongDocumentStorage.Load(path);
			((ScriptSequenceDefinition)opened.Objects[generated.Id]).Source
				.Should().Be(savedSource);
		}
		finally
		{
			if (File.Exists(path)) File.Delete(path);
		}
	}

	[Test]
	public void CreateScriptPatternAddsCanonicalPatternPlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated",
				rowCount: 32,
				channelCount: 4);

		pattern.Name.Should().Be("Generated");
		pattern.RowCount.Should().Be(32);
		pattern.ChannelCount.Should().Be(4);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
		workspace.Document.GetSectionRoot(SongTreeSection.Patterns)
			.Children.Should().ContainSingle();
	}

	[Test]
	public void CreateScriptSequenceAddsCanonicalSequencePlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();

		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Generated");

		sequence.Name.Should().Be("Generated");
		workspace.Document.GetSectionRoot(SongTreeSection.Sequences)
			.Children.Should().ContainSingle();
		workspace.Document.AudioRevision.Should().Be(1);
	}

	[Test]
	public void UpdateSourceMarksAudioAndEqualSourceIsNoOp()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		ScriptDocumentEditor.UpdateSource(
			workspace,
			pattern,
			"yield return _O(12);");

		pattern.Source.Should().Be("yield return _O(12);");
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		ScriptDocumentEditor.UpdateSource(
			workspace,
			pattern,
			"yield return _O(12);");

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void UpdateScriptPatternLayoutUsesSameValidationAsDataPatterns()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");

		ScriptDocumentEditor.UpdatePatternLayout(
			workspace,
			pattern,
			rowCount: 48,
			channelCount: 6,
			minorHighlightRows: 3,
			majorHighlightRows: 12);

		pattern.RowCount.Should().Be(48);
		pattern.ChannelCount.Should().Be(6);
		pattern.MinorHighlightRows.Should().Be(3);
		pattern.MajorHighlightRows.Should().Be(12);
	}

	[TestCase(-1, 1, 4, 16)]
	[TestCase(1, 0, 4, 16)]
	[TestCase(1, 1, -1, 16)]
	[TestCase(1, 1, 4, -1)]
	public void InvalidScriptPatternLayoutIsRejected(
		int rows,
		int channels,
		int minor,
		int major)
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");

		var action = () =>
			ScriptDocumentEditor.UpdatePatternLayout(
				workspace,
				pattern,
				rows,
				channels,
				minor,
				major);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void ScriptSequenceCanBeSetAsRoot()
	{
		DocumentWorkspace workspace = new();
		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Generated");

		ScriptDocumentEditor.SetRootSequence(
			workspace,
			sequence);

		workspace.Document.RootSequenceId.Should().Be(sequence.Id);
	}

	[Test]
	public void FormattingObjectReferenceUsesCanonicalPersistedSyntax()
	{
		ScriptObjectReferenceOption option =
			new((ObjectId)137U, "Piano", SongObjectKind.Sample);

		option.ReferenceText.Should().Be(
			ScriptObjectReferenceSyntax.Format((ObjectId)137U));
		option.ReferenceText.Should().Be("_O(137)");
		option.ToString().Should().Contain("Piano");
		option.ToString().Should().Contain("137");
	}

	[Test]
	public void ReferenceCatalogContainsEveryLiveSongObject()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Script");
		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Sequence");

		ScriptObjectReferenceOption[] options =
			ScriptDocumentEditor.GetObjectReferences(
				workspace.Document);

		options.Should().Contain(option => option.Id == pattern.Id);
		options.Should().Contain(option => option.Id == sequence.Id);
		options.Should().HaveCount(workspace.Document.Objects.Count);
	}

	[Test]
	public void EditingForeignScriptObjectIsRejected()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition foreign =
			new((ObjectId)999U, "Foreign");

		var action = () =>
			ScriptDocumentEditor.UpdateSource(
				workspace,
				foreign,
				"source");

		action.Should().Throw<InvalidOperationException>();
	}
	[Test]
	public void InsertReferenceReplacesSelectionAndReturnsCaretAfterAtomicSyntax()
	{
		ScriptReferenceInsertion result =
			ScriptDocumentEditor.InsertObjectReference(
				"before SELECTED after",
				selectionStart: 7,
				selectionEnd: 15,
				(ObjectId)42U);

		result.Text.Should().Be("before _O(42) after");
		result.Caret.Should().Be(13);
	}

	[Test]
	public void InsertReferenceNormalizesReverseSelection()
	{
		ScriptReferenceInsertion result =
			ScriptDocumentEditor.InsertObjectReference(
				"abcdef",
				selectionStart: 5,
				selectionEnd: 2,
				(ObjectId)7U);

		result.Text.Should().Be("ab_O(7)f");
		result.Caret.Should().Be(7);
	}


	[Test]
	public void SourceAnalysisProjectsSemanticReferencesAndDiagnostics()
	{
		DocumentWorkspace workspace = new();

		ObjectId liveId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(
				liveId,
				"Live Pattern"));

		ObjectId deletedId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptSequenceDefinition(
				deletedId,
				"Deleted Sequence"));
		workspace.Document.Remove(deletedId);

		const string source =
			"// _O(999)\n"
			+ "_O(1);\n"
			+ "_O(2);\n"
			+ "_O(77);\n"
			+ "_O();";

		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		analysis.IsReliable.Should().BeFalse();
		analysis.Syntax.Diagnostics.Should().ContainSingle(
			diagnostic =>
				diagnostic.Code == "HRS1001"
				&& diagnostic.Severity
					== ScriptDiagnosticSeverity.Error);

		analysis.References.Should().HaveCount(3);
		analysis.References[0].Id.Should().Be(liveId);
		analysis.References[0].DisplayName.Should().Be("Live Pattern");
		analysis.References[0].Resolution.Should().Be(
			ScriptObjectReferenceResolution.Live);

		analysis.References[1].Id.Should().Be(deletedId);
		analysis.References[1].DisplayName.Should().Be("Deleted Sequence");
		analysis.References[1].Resolution.Should().Be(
			ScriptObjectReferenceResolution.Tombstone);

		analysis.References[2].Id.Should().Be((ObjectId)77U);
		analysis.References[2].DisplayName.Should().Be("_O(77)");
		analysis.References[2].Resolution.Should().Be(
			ScriptObjectReferenceResolution.Missing);
	}
	[Test]
	public void SourceAnalysisIncludesExecutableCompilerDiagnostics()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Script");

		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				pattern,
				"System.IO.File.ReadAllText(\"not-allowed\");");

		analysis.IsReliable.Should().BeFalse();
		analysis.Diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "HRS2001"
				&& diagnostic.Severity
					== ScriptDiagnosticSeverity.Error);
	}


}
