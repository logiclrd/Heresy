using System;
using System.Threading.Tasks;

namespace Heresy.Render.Realtime;

public sealed class BackgroundPlaybackController
	: IDisposable
{
	public BackgroundPlaybackController(
		IAudioOutputBackend backend,
		IBackgroundPlaybackSourceFactory sourceFactory)
	{
		throw new NotImplementedException();
	}

	public Task PlayAsync(PlaybackRequest request)
		=> throw new NotImplementedException();

	public Task StopAsync()
		=> throw new NotImplementedException();

	public void Dispose()
	{
	}
}
