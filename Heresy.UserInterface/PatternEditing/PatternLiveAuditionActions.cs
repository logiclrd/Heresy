using System;
using System.Threading.Tasks;

using Heresy.Core.Sequencing;

namespace Heresy.UserInterface.PatternEditing;

public sealed class PatternLiveAuditionActions
{
	public PatternLiveAuditionActions(
		Func<Task> beginSessionAsync,
		Func<int, StartNoteCommand, Task> startNoteAsync,
		Func<int, Task> releaseNoteAsync)
	{
		BeginSessionAsync =
			beginSessionAsync
				?? throw new ArgumentNullException(nameof(beginSessionAsync));
		StartNoteAsync =
			startNoteAsync
				?? throw new ArgumentNullException(nameof(startNoteAsync));
		ReleaseNoteAsync =
			releaseNoteAsync
				?? throw new ArgumentNullException(nameof(releaseNoteAsync));
	}

	public Func<Task> BeginSessionAsync { get; }

	public Func<int, StartNoteCommand, Task> StartNoteAsync { get; }

	public Func<int, Task> ReleaseNoteAsync { get; }
}
