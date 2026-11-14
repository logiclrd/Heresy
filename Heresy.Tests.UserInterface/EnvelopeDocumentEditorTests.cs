using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class EnvelopeDocumentEditorTests
{
	[Test]
	public void ScriptEnvelopeOnlyNotifiesOnSuccessfulAudioCommit()
	{
		DocumentWorkspace workspace = new();
		var notifications = new System.Collections.Generic.List<SharedAudioObjectCommittedEventArgs>();
		workspace.SharedAudioObjectCommitted += (_, e) => notifications.Add(e);
		ScriptEnvelopeDefinition script = EnvelopeDocumentEditor.CreateScriptEnvelope(
			workspace, "Expression");
		notifications.Should().BeEmpty();
		EnvelopeDocumentEditor.UpdateScriptEnvelope(
			workspace, script, "0.75", TimeSpan.FromSeconds(2));
		notifications.Should().ContainSingle();
		notifications[0].ObjectId.Should().Be(script.Id);
		notifications[0].Document.Should().BeSameAs(workspace.Document);
		notifications[0].AudioRevision.Should().Be(workspace.Document.AudioRevision);
		EnvelopeDocumentEditor.UpdateScriptEnvelope(
			workspace, script, "0.75", TimeSpan.FromSeconds(2));
		notifications.Should().ContainSingle();
		var invalid = () => EnvelopeDocumentEditor.UpdateScriptEnvelope(
			workspace, script, "System.IO.File.ReadAllText(\"secret\")", null);
		invalid.Should().Throw<ArgumentException>();
		notifications.Should().ContainSingle();
	}

	[Test]
	public void ScriptEnvelopeCreationEditingAndFailedValidationAreAtomic()
	{
		DocumentWorkspace workspace = new();
		ScriptEnvelopeDefinition created =
			EnvelopeDocumentEditor.CreateScriptEnvelope(workspace,
				"Expression", "releaseSeconds < 0 ? 1.0 : 0.0",
				TimeSpan.FromSeconds(1));
		created.Kind.Should().Be(SongObjectKind.Envelope);
		workspace.Document.GetSectionRoot(SongTreeSection.Envelopes)
			.Children.Cast<SongTreeObject>().Single().ObjectId.Should().Be(created.Id);
		uint revision = workspace.Document.AudioRevision;
		var invalid = () => EnvelopeDocumentEditor.UpdateScriptEnvelope(
			workspace, created, "System.IO.File.ReadAllText(\"secret\")",
			TimeSpan.Zero);
		invalid.Should().Throw<ArgumentException>();
		created.Source.Should().Be("releaseSeconds < 0 ? 1.0 : 0.0");
		workspace.Document.AudioRevision.Should().Be(revision);
		EnvelopeDocumentEditor.UpdateScriptEnvelope(workspace,
			created, "Math.Sin(elapsedSeconds)", null);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		created.ReleaseDuration.Should().BeNull();
		EnvelopeDocumentEditor.UpdateScriptEnvelope(workspace,
			created, "Math.Sin(elapsedSeconds)", null);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}

}
