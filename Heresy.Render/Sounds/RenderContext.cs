using System;

using Heresy.Render.Configuration;

namespace Heresy.Render.Sounds;

public sealed class RenderContext
{
	public RenderContext(RenderConfiguration configuration)
	{
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
	}

	public RenderConfiguration Configuration { get; }
}
