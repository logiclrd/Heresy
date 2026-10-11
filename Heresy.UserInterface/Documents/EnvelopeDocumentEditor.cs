using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent ADSR envelope authoring operations used by the
/// Avalonia UI.
/// </summary>
public static class EnvelopeDocumentEditor
{
	public static AdsrEnvelopeDefinition CreateAdsrEnvelope(
		DocumentWorkspace workspace,
		string name,
		TimeSpan attack = default,
		TimeSpan decay = default,
		double sustainLevel = 1.0,
		TimeSpan release = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidateValues(attack, decay, sustainLevel, release);

		ObjectId id = workspace.Document.AllocateObjectId();
		AdsrEnvelopeDefinition envelope =
			new(id, name.Trim())
			{
				Attack = attack,
				Decay = decay,
				SustainLevel = sustainLevel,
				Release = release,
			};
		workspace.Document.Add(envelope, affectsAudio: true);
		return envelope;
	}

	public static void UpdateAdsrEnvelope(
		DocumentWorkspace workspace,
		AdsrEnvelopeDefinition envelope,
		TimeSpan attack,
		TimeSpan decay,
		double sustainLevel,
		TimeSpan release)
	{
		ValidateEnvelope(workspace, envelope);
		ValidateValues(attack, decay, sustainLevel, release);

		if (envelope.Attack == attack
			&& envelope.Decay == decay
			&& envelope.SustainLevel == sustainLevel
			&& envelope.Release == release)
		{
			return;
		}

		envelope.Attack = attack;
		envelope.Decay = decay;
		envelope.SustainLevel = sustainLevel;
		envelope.Release = release;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	/// <summary>Create a node envelope with a fully validated shape.
	/// This is the new curve model; existing ADSR entry points are temporary
	/// until the renderer and graphical editors complete their cutover.</summary>
	public static NodeEnvelopeDefinition CreateNodeEnvelope(
		DocumentWorkspace workspace,
		string name,
		IEnumerable<EnvelopeNode>? nodes = null,
		int sustainStartIndex = 2,
		int sustainEndIndex = 2,
		EnvelopeSustainLoopMode sustainLoopMode = EnvelopeSustainLoopMode.Forward)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		// Validate before reserving an ObjectId or mutating the document.
		NodeEnvelopeDefinition candidate = new(
			(ObjectId)1U, name.Trim());
		if (nodes is not null)
			candidate.ReplaceCurve(nodes, sustainStartIndex,
				sustainEndIndex, sustainLoopMode);
		else if (sustainStartIndex != 2 || sustainEndIndex != 2
			|| sustainLoopMode != EnvelopeSustainLoopMode.Forward)
			candidate.ReplaceCurve(candidate.Nodes, sustainStartIndex,
				sustainEndIndex, sustainLoopMode);
		NodeEnvelopeDefinition created = new(
			workspace.Document.AllocateObjectId(), name.Trim());
		created.ReplaceCurve(candidate.Nodes, candidate.SustainStartIndex,
			candidate.SustainEndIndex, candidate.SustainLoopMode);
		workspace.Document.Add(created, affectsAudio: true);
		return created;
	}

	public static void UpdateNodeEnvelope(
		DocumentWorkspace workspace,
		NodeEnvelopeDefinition envelope,
		IEnumerable<EnvelopeNode> nodes,
		int sustainStartIndex,
		int sustainEndIndex,
		EnvelopeSustainLoopMode sustainLoopMode)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentNullException.ThrowIfNull(nodes);
		if (!workspace.Document.TryGet(envelope.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, envelope))
			throw new InvalidOperationException(
				"The envelope is not part of the active song document.");

		EnvelopeNode[] copy = nodes.ToArray();
		NodeEnvelopeDefinition.Validate(copy, sustainStartIndex,
			sustainEndIndex, sustainLoopMode);
		if (envelope.SustainStartIndex == sustainStartIndex
			&& envelope.SustainEndIndex == sustainEndIndex
			&& envelope.SustainLoopMode == sustainLoopMode
			&& envelope.Nodes.SequenceEqual(copy))
			return;
		envelope.ReplaceCurve(copy, sustainStartIndex, sustainEndIndex,
			sustainLoopMode);
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	/// <summary>
	/// Create a script expression Envelope only after successful analysis.
	/// An invalid script never changes the workspace or consumes an object ID.
	/// </summary>
	public static ScriptEnvelopeDefinition CreateScriptEnvelope(
		DocumentWorkspace workspace, string name,
		string source = "releaseSeconds < 0 ? 1.0 : 0.0",
		TimeSpan? releaseDuration = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidateScript(source, releaseDuration);
		ScriptEnvelopeDefinition envelope =
			new(workspace.Document.AllocateObjectId(), name.Trim())
			{
				Source = source,
				ReleaseDuration = releaseDuration,
			};
		workspace.Document.Add(envelope, affectsAudio: true);
		return envelope;
	}

	public static void UpdateScriptEnvelope(
		DocumentWorkspace workspace, ScriptEnvelopeDefinition envelope,
		string source, TimeSpan? releaseDuration)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(envelope);
		if (!workspace.Document.TryGet(envelope.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, envelope))
			throw new InvalidOperationException(
				"The envelope is not part of the active song document.");
		ValidateScript(source, releaseDuration);
		if (envelope.Source == source
			&& envelope.ReleaseDuration == releaseDuration)
			return;
		envelope.Source = source;
		envelope.ReleaseDuration = releaseDuration;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	private static void ValidateScript(string source, TimeSpan? releaseDuration)
	{
		ArgumentNullException.ThrowIfNull(source);
		if (releaseDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(releaseDuration));
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
			ScriptCompiler.AnalyzeEnvelopeSource(source);
		if (diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error))
			throw new ArgumentException(
				string.Join("; ", diagnostics.Select(d => d.Message)),
				nameof(source));
	}

	private static void ValidateEnvelope(
		DocumentWorkspace workspace,
		AdsrEnvelopeDefinition envelope)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(envelope);

		if (!workspace.Document.TryGet(
			envelope.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, envelope))
		{
			throw new InvalidOperationException(
				"The envelope is not part of the active song document.");
		}
	}

	private static void ValidateValues(
		TimeSpan attack,
		TimeSpan decay,
		double sustainLevel,
		TimeSpan release)
	{
		if (attack < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(attack));
		if (decay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(decay));
		if (release < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(release));
		if (double.IsNaN(sustainLevel)
			|| double.IsInfinity(sustainLevel))
		{
			throw new ArgumentOutOfRangeException(nameof(sustainLevel));
		}
	}
}
