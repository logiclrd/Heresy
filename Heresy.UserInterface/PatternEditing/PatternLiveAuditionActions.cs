using System;
using System.Threading.Tasks;

using Heresy.Core.Sequencing;

namespace Heresy.UserInterface.PatternEditing;

public sealed record PatternLiveAuditionActions(
	Func<Task> BeginSessionAsync,
	Func<int, StartNoteCommand, Task> StartNoteAsync,
	Func<int, Task> ReleaseNoteAsync)
{
	public PatternLiveAuditionActions
	{
		ArgumentNullException.ThrowIfNull(BeginSessionAsync);
		ArgumentNullException.ThrowIfNull(StartNoteAsync);
		ArgumentNullException.ThrowIfNull(ReleaseNoteAsync);
	}
}
