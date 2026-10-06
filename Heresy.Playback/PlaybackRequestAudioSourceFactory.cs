using System;
using System.Collections.Generic;

using Heresy.Render.Configuration;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.Scripting.Analysis;

namespace Heresy.Playback;

public sealed class PlaybackSourceCompilationException
	: InvalidOperationException
{
	public PlaybackSourceCompilationException(
		string message,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
		: base(message)
	{
		Diagnostics =
			diagnostics
				?? throw new ArgumentNullException(nameof(diagnostics));
	}

	public IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics { get; }
}

public sealed class PlaybackRequestAudioSourceFactory
	: IBackgroundPlaybackSourceFactory
{
	public PlaybackRequestAudioSourceFactory(
		RenderConfiguration configuration,
		ISampleDataProvider sampleDataProvider)
	{
		throw new NotImplementedException();
	}

	public IAudioOutputSource Create(
		PlaybackRequest request)
		=> throw new NotImplementedException();
}
