using System;
using System.Collections.Generic;

using Heresy.Core.Diagnostics;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Transactional receiver for one sequencer invocation. The schedule is not
/// published until Freeze succeeds.
/// </summary>
public sealed class NoteScheduleBuilder : INoteReceiver
{
	public const int MaximumGeneratedNotes = 1_000_000;

	private readonly List<NoteEvent> _events = [];
	private bool _frozen;
	private long _nextEmissionOrder;

	public int Count => _events.Count;

	public void Append(NoteEvent noteEvent)
	{
		ArgumentNullException.ThrowIfNull(noteEvent);

		if (_frozen)
			throw new InvalidOperationException("The note schedule has already been frozen.");

		if (_events.Count >= MaximumGeneratedNotes)
		{
			throw new SequencingResourceLimitException(
				$"A sequencer invocation emitted more than {MaximumGeneratedNotes:N0} note events.");
		}

		_events.Add(noteEvent.WithEmissionOrder(_nextEmissionOrder++));
	}

	public NoteSchedule Freeze()
	{
		if (_frozen)
			throw new InvalidOperationException("The note schedule has already been frozen.");

		_frozen = true;
		return new NoteSchedule(_events.ToArray());
	}
}
