using System;
using System.Threading.Tasks;

using Heresy.Render.Realtime;

namespace Heresy.UserInterface.PatternEditing;

public sealed class PatternLiveAuditionActions
{
	public PatternLiveAuditionActions(
		Func<LivePlaybackEvent, Task> sendEventAsync)
	{
		SendEventAsync =
			sendEventAsync
				?? throw new ArgumentNullException(nameof(sendEventAsync));
	}

	public Func<LivePlaybackEvent, Task> SendEventAsync { get; }
}
