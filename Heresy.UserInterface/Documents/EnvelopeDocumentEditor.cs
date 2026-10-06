using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;

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
