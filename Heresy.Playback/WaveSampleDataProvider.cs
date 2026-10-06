using System;

using Heresy.Core.Samples;
using Heresy.Render.Samples;

namespace Heresy.Playback;

public sealed class WaveSampleDataProvider
	: ISampleDataProvider
{
	public ISampleData GetSampleData(
		SampleDefinition sample)
		=> throw new NotImplementedException();
}
