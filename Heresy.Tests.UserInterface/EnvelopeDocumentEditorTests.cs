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
	public void CreateAdsrEnvelopeAddsCanonicalEnvelopePlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(
				workspace,
				"Volume Shape",
				TimeSpan.FromMilliseconds(10),
				TimeSpan.FromMilliseconds(20),
				sustainLevel: 0.75,
				TimeSpan.FromMilliseconds(30));

		envelope.Name.Should().Be("Volume Shape");
		envelope.Attack.Should().Be(TimeSpan.FromMilliseconds(10));
		envelope.Decay.Should().Be(TimeSpan.FromMilliseconds(20));
		envelope.SustainLevel.Should().Be(0.75);
		envelope.Release.Should().Be(TimeSpan.FromMilliseconds(30));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		SongTreeObject node =
			workspace.Document.GetSectionRoot(SongTreeSection.Envelopes)
				.Children.Cast<SongTreeObject>()
				.Single();
		node.ObjectId.Should().Be(envelope.Id);
	}

	[Test]
	public void UpdateAdsrEnvelopeMarksAudioAndSameValueIsNoOp()
	{
		DocumentWorkspace workspace = new();
		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(
				workspace,
				"Envelope");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		EnvelopeDocumentEditor.UpdateAdsrEnvelope(
			workspace,
			envelope,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(2),
			sustainLevel: -0.5,
			TimeSpan.FromSeconds(3));

		envelope.Attack.Should().Be(TimeSpan.FromSeconds(1));
		envelope.Decay.Should().Be(TimeSpan.FromSeconds(2));
		envelope.SustainLevel.Should().Be(-0.5);
		envelope.Release.Should().Be(TimeSpan.FromSeconds(3));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		EnvelopeDocumentEditor.UpdateAdsrEnvelope(
			workspace,
			envelope,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(2),
			sustainLevel: -0.5,
			TimeSpan.FromSeconds(3));

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[TestCase(-1, 0, 0)]
	[TestCase(0, -1, 0)]
	[TestCase(0, 0, -1)]
	public void NegativeDurationsAreRejected(
		double attackSeconds,
		double decaySeconds,
		double releaseSeconds)
	{
		DocumentWorkspace workspace = new();
		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(
				workspace,
				"Envelope");

		var action = () =>
			EnvelopeDocumentEditor.UpdateAdsrEnvelope(
				workspace,
				envelope,
				TimeSpan.FromSeconds(attackSeconds),
				TimeSpan.FromSeconds(decaySeconds),
				sustainLevel: 1.0,
				TimeSpan.FromSeconds(releaseSeconds));

		action.Should().Throw<ArgumentOutOfRangeException>();
		envelope.Attack.Should().Be(TimeSpan.Zero);
		envelope.Decay.Should().Be(TimeSpan.Zero);
		envelope.Release.Should().Be(TimeSpan.Zero);
	}

	[TestCase(double.NaN)]
	[TestCase(double.PositiveInfinity)]
	[TestCase(double.NegativeInfinity)]
	public void NonFiniteSustainIsRejected(double sustainLevel)
	{
		DocumentWorkspace workspace = new();
		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(
				workspace,
				"Envelope");

		var action = () =>
			EnvelopeDocumentEditor.UpdateAdsrEnvelope(
				workspace,
				envelope,
				TimeSpan.Zero,
				TimeSpan.Zero,
				sustainLevel,
				TimeSpan.Zero);

		action.Should().Throw<ArgumentOutOfRangeException>();
		envelope.SustainLevel.Should().Be(1.0);
	}

	[Test]
	public void NegativeAndAboveUnitySustainLevelsRemainLegal()
	{
		DocumentWorkspace workspace = new();
		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(
				workspace,
				"Envelope",
				sustainLevel: -0.25);

		envelope.SustainLevel.Should().Be(-0.25);

		EnvelopeDocumentEditor.UpdateAdsrEnvelope(
			workspace,
			envelope,
			TimeSpan.Zero,
			TimeSpan.Zero,
			sustainLevel: 1.5,
			TimeSpan.Zero);

		envelope.SustainLevel.Should().Be(1.5);
	}

	[Test]
	public void EditingEnvelopeOutsideActiveDocumentIsRejected()
	{
		DocumentWorkspace workspace = new();
		AdsrEnvelopeDefinition foreign =
			new((ObjectId)1U, "Foreign");

		var action = () =>
			EnvelopeDocumentEditor.UpdateAdsrEnvelope(
				workspace,
				foreign,
				TimeSpan.Zero,
				TimeSpan.Zero,
				1.0,
				TimeSpan.Zero);

		action.Should().Throw<InvalidOperationException>();
	}
}
