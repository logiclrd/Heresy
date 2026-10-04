using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Envelopes;

public sealed class AdsrEnvelopeDefinition : EnvelopeDefinition
{
	public AdsrEnvelopeDefinition(ObjectId id, string name) : base(id, name) { }

	public TimeSpan Attack { get; set; }
	public TimeSpan Decay { get; set; }
	public double SustainLevel { get; set; } = 1.0;
	public TimeSpan Release { get; set; }
}
