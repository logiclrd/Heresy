using Heresy.Core.Samples;

namespace Heresy.Render.Samples;

/// <summary>
/// Resolves persistent sample definitions to decoded PCM.
/// </summary>
public interface ISampleDataProvider
{
	ISampleData GetSampleData(SampleDefinition sample);
}
