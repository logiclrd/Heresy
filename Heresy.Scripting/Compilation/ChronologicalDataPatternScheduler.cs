using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Sequences;
using Heresy.Core.Timing;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Chronological, tick-driven data-pattern flattening. Each invocation owns
/// an independently advancing row cursor, but all mapped cursors observe one
/// SequencingState and channel-state map. No future row is compiled early.
/// Limited to data patterns without pattern control/delays or complex effects;
/// other sources continue using the general pattern/sequence compiler.
/// </summary>
internal static class ChronologicalDataPatternScheduler
{
	private sealed class RowCursor
	{
		public required ObjectId Id { get; init; }
		public required PatternDefinition Pattern { get; init; }
		public required SequencingContext Context { get; init; }
		public required NoteSchedule Raw { get; init; }
		public required IReadOnlySet<ObjectId> Ancestry { get; init; }
		public double DueTick { get; set; }
		public double RowStartTick { get; set; }
		public double RowEndTick { get; set; }
		public bool InScriptRow { get; set; }
		public int ScriptEventIndex { get; set; }
		public List<NoteEvent> ScriptRowEvents { get; } = [];
		public int Row { get; set; }
		public long Sequence { get; init; }
		public bool IsRoot { get; init; }
		public int RootOrder { get; init; }
		public List<NoteEvent> EndOfRowCommands { get; } = [];
	}

	private sealed class RowSlice : IDeferredSourcePatternGenerator
	{
		private readonly IReadOnlyList<NoteEvent> _events;
		public RowSlice(NoteSchedule raw, int row)
		{
			List<NoteEvent> selected = [];
			foreach (NoteEvent note in raw)
			{
				if (note.Offset.RowOffset != row)
					continue;
				selected.Add(note with
				{
					Offset = new MusicalTime(TimeSpan.Zero, 0),
				});
			}
			_events = selected;
		}

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			foreach (NoteEvent note in _events)
				output.Append(note);
			rowCount = 1.0;
		}
	}

	// Scripted timing commands retain PatternNoteProcessor's established
	// row-boundary semantics: Tempo(1.5, ...) executes at row 1, not at 1.5.
	// Other supported script events keep their exact fractional-row tick.
	private static double ScriptEventDueRow(NoteEvent note)
		=> note.Target.Kind == ChannelTargetKind.Global
			&& note.Commands.Count > 0
			&& note.Commands.All(command => command is SetTempoCommand)
				? Math.Floor(note.Offset.RowOffset)
				: note.Offset.RowOffset;

	private sealed class ScriptEventSlice : IRawPatternNoteGenerator
	{
		private readonly NoteEvent _event;

		public ScriptEventSlice(NoteEvent noteEvent)
			=> _event = noteEvent with
			{
				Offset = new MusicalTime(TimeSpan.Zero, 0),
			};

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			output.Append(_event);
			rowCount = 1;
		}
	}

	public static bool CanHandle(
		SongDocument document,
		PatternDefinition root)
		=> IsCompatible(document, root, out bool hasNested) && hasNested;

	public static bool CanHandleSequence(
		SongDocument document,
		DataSequenceDefinition sequence)
	{
		ArgumentNullException.ThrowIfNull(sequence);
		bool hasNested = false;
		foreach (SequenceEntry entry in sequence.Entries)
		{
			if (entry.StartRow != 0
				|| !document.TryGet(entry.PatternId, out SongObject? obj)
				|| obj is not PatternDefinition pattern
				|| !IsCompatible(document, pattern, out bool nested))
				return false;
			hasNested |= nested;
		}
		return sequence.Entries.Count != 0 && hasNested;
	}

	private static bool IsCompatible(
		SongDocument document,
		PatternDefinition root,
		out bool hasNested)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(root);
		HashSet<ObjectId> visited = [];
		bool foundNested = false;

		bool Visit(PatternDefinition pattern)
		{
			if (!visited.Add(pattern.Id))
				return true;

			if (pattern is DataPatternDefinition data)
			{
				foreach ((_, _, PatternCell cell) in
					data.Grid.EnumerateNonEmptyCells())
				{
					foreach (PatternEffect effect in cell.Effects)
					{
						if (effect is not (SetTempoPatternEffect
							or SetSpeedPatternEffect
							or TrackerVolumeSlidePatternEffect
							or EmptyTrackerPatternEffect))
							return false;
					}
					if (cell.Note is not StartPatternNote start || start.Mixdown)
						continue;
					ObjectId id = !cell.SourceId.IsNone
						? cell.SourceId : start.SourceId;
					if (!VisitSource(id))
						return false;
				}
				return true;
			}

			if (pattern is ScriptPatternDefinition script)
			{
				ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
					ScriptCompiler.CompilePattern(script);
				if (!compilation.Success || compilation.Program is null)
					return false;
				NoteScheduleBuilder rawBuilder = new();
				compilation.Program.GenerateRawNotes(
					new SequencingContext(), rawBuilder, out double rowCount);
				if (rowCount != script.RowCount || rowCount <= 0)
					return false;
				foreach (NoteEvent note in rawBuilder.Freeze())
				{
					if (note.Offset.TimeOffset != TimeSpan.Zero
						|| !double.IsFinite(note.Offset.RowOffset)
						|| note.Offset.RowOffset < 0
						// A command exactly at the final boundary executes at
						// that boundary, after the final row's elapsed ticks.
						|| note.Offset.RowOffset > rowCount)
						return false;
					// Only standalone global SetTempo commands are eligible.
					// In particular, scripted Speed, ramps, and mixed-command
					// events are not yet part of the shared-tick contract.
					if (note.Target.Kind == ChannelTargetKind.Global)
					{
						if (note.Commands.Count == 0
							|| note.Commands.Any(command =>
								command is not SetTempoCommand))
							return false;
						continue;
					}
					if (note.Target.Kind != ChannelTargetKind.Physical)
						return false;
					foreach (NoteCommand command in note.Commands)
					{
						if (command is StartNoteCommand start)
						{
							if (!start.Mixdown && !VisitSource(start.SourceId))
								return false;
						}
						else if (command is not (NoteCutCommand or NoteOffCommand))
							return false;
					}
				}
				return true;
			}
			return false;
		}

		bool VisitSource(ObjectId id)
		{
			if (id.IsNone || !document.TryGet(id, out SongObject? obj))
				return true;
			if (obj is PatternDefinition child)
			{
				foundNested = true;
				return Visit(child);
			}
			return obj is not SequenceDefinition;
		}

		bool compatible = Visit(root);
		hasNested = foundNested;
		return compatible;
	}

	public static SongScheduleCompilationResult Compile(
		SongDocument document,
		PatternDefinition root,
		SequencingContext? suppliedContext)
		=> Run(document, [root], suppliedContext, sequenceMode: false);

	public static SongScheduleCompilationResult CompileSequence(
		SongDocument document,
		DataSequenceDefinition sequence,
		SequencingContext? suppliedContext)
	{
		List<PatternDefinition> patterns = [];
		foreach (SequenceEntry entry in sequence.Entries)
		{
			if (!document.TryGet(entry.PatternId, out SongObject? obj)
				|| obj is not PatternDefinition pattern)
				throw new InvalidOperationException(
					"Eligible arrangement pattern disappeared while preparing its cursors.");
			patterns.Add(pattern);
		}
		return Run(document, patterns, suppliedContext, sequenceMode: true);
	}

	private static SongScheduleCompilationResult Run(
		SongDocument document,
		IReadOnlyList<PatternDefinition> patterns,
		SequencingContext? suppliedContext,
		bool sequenceMode)
	{
		SequencingContext context = suppliedContext ?? new SequencingContext();
		if (context.FlattenedSourceExpander is not null
			|| context.IsPreparingFlattenedChild)
			throw new InvalidOperationException(
				"Chronological data pattern cursors require a root context.");
		context.ResolvePatternSourcesAtRowTime = true;
		List<RowCursor> active = [];
		Dictionary<ObjectId, IRawPatternNoteGenerator> compiledScripts = [];
		NoteScheduleBuilder output = new();
		List<CompiledPatternPlaybackPosition> positions = [];
		long nextCursorSequence = 0;
		double tick = 0;
		TimeSpan elapsed = TimeSpan.Zero;
		int workCount = 0;

		RowCursor CreateCursor(
			ObjectId id,
			PatternDefinition pattern,
			SequencingContext mapped,
			IReadOnlySet<ObjectId> ancestry,
			bool isRoot = false,
			int rootOrder = 0)
		{
			if (ancestry.Contains(id))
				throw new InvalidOperationException(
					$"Flattened Pattern/Sequence sound source cycle includes object {id.Value}.");
			HashSet<ObjectId> descendants = new(ancestry) { id };
			NoteScheduleBuilder rawBuilder = new();
			if (pattern is DataPatternDefinition data)
				data.GenerateRawNotes(mapped, rawBuilder, out _);
			else if (pattern is ScriptPatternDefinition script)
			{
				if (!compiledScripts.TryGetValue(pattern.Id, out
					IRawPatternNoteGenerator? compiled))
				{
					ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
						ScriptCompiler.CompilePattern(script);
					if (!compilation.Success || compilation.Program is null)
						throw new InvalidOperationException(
							$"Could not compile scripted Pattern {pattern.Id.Value}.");
					compiled = compilation.Program;
					compiledScripts.Add(pattern.Id, compiled);
				}
				compiled.GenerateRawNotes(mapped, rawBuilder, out _);
			}
			else
				throw new NotSupportedException("Unknown pattern cursor source.");
			return new RowCursor
			{
				Id = id,
				Pattern = pattern,
				Context = mapped,
				Raw = rawBuilder.Freeze(),
				Ancestry = descendants,
				DueTick = tick,
				Sequence = nextCursorSequence++,
				IsRoot = isRoot,
				RootOrder = rootOrder,
			};
		}

		PatternDefinition first = patterns[0];
		active.Add(CreateCursor(first.Id, first, context,
			new HashSet<ObjectId>(), isRoot: true));
		double tempo = context.State.Tempo;
		while (active.Count != 0)
		{
			double next = active.Min(cursor => cursor.DueTick);
			if (next < tick)
				throw new InvalidOperationException("A nested row cursor moved backwards.");
			if (next != tick)
			{
				elapsed += TimeSpan.FromSeconds(
					(next - tick) * SequencingConstants.Diachron.TotalSeconds / tempo);
				tick = next;
			}

			// A row may start another cursor at the same tick. Keep draining
			// that tick before advancing time. Mapped channel and creation
			// order provide deterministic simultaneous event ordering.
			while (true)
			{
				RowCursor? current = active
					.Where(cursor => cursor.DueTick == tick)
					.OrderBy(cursor => cursor.Context.PhysicalChannelBase)
					.ThenBy(cursor => cursor.Sequence)
					.FirstOrDefault();
				if (current is null)
					break;
				// Every cursor operation counts, including arbitrarily many
				// fractional events at the same tick (the outer clock need
				// not advance between these operations).
				if (++workCount > NoteScheduleBuilder.MaximumGeneratedNotes)
					throw new InvalidOperationException(
						"Chronological flattened row expansion exceeded the sequencing resource limit.");

				active.Remove(current);
				foreach (NoteEvent endOfRow in current.EndOfRowCommands)
				{
					output.Append(endOfRow with
					{
						Offset = new MusicalTime(elapsed, 0.0),
					});
				}
				current.EndOfRowCommands.Clear();

				if (current.Row >= current.Pattern.RowCount)
				{
					if (current.IsRoot && current.RootOrder + 1 < patterns.Count)
				{
						int nextOrder = current.RootOrder + 1;
						PatternDefinition nextRoot = patterns[nextOrder];
						active.Add(CreateCursor(
							nextRoot.Id, nextRoot, context,
							new HashSet<ObjectId>(), isRoot: true,
							rootOrder: nextOrder));
					}
					continue;
				}

				if (current.IsRoot)
					positions.Add(new CompiledPatternPlaybackPosition(
						elapsed, current.Id, current.Row,
						sequenceMode ? current.RootOrder : null));

				NoteScheduleBuilder rowBuilder = new();
				TimeSpan nominalDuration;
				if (current.Pattern is ScriptPatternDefinition)
				{
					if (!current.InScriptRow)
					{
						current.InScriptRow = true;
						current.RowStartTick = tick;
						current.RowEndTick = tick + context.State.Speed;
						current.ScriptEventIndex = 0;
						current.ScriptRowEvents.Clear();
						foreach (NoteEvent raw in current.Raw)
						{
							// The final row also owns endpoint events at exactly
							// RowCount. They execute at RowEndTick, rather than
							// disappearing when the cursor retires.
							if (Math.Floor(raw.Offset.RowOffset) == current.Row
								|| current.Row == current.Pattern.RowCount - 1
									&& raw.Offset.RowOffset == current.Pattern.RowCount)
								current.ScriptRowEvents.Add(raw);
						}
						current.ScriptRowEvents.Sort((a, b) =>
						{
							int compare = ScriptEventDueRow(a).CompareTo(ScriptEventDueRow(b));
							if (compare != 0)
								return compare;
							compare = a.Target.PhysicalChannel.CompareTo(
								b.Target.PhysicalChannel);
							return compare != 0 ? compare
								: a.EmissionOrder.CompareTo(b.EmissionOrder);
						});
					}

					if (current.ScriptEventIndex >= current.ScriptRowEvents.Count)
					{
						current.Row++;
						current.InScriptRow = false;
						current.DueTick = current.RowEndTick;
						active.Add(current);
						continue;
					}

					NoteEvent scripted = current.ScriptRowEvents[current.ScriptEventIndex];
					double eventTick = current.RowStartTick
						+ (ScriptEventDueRow(scripted) - current.Row)
							* (current.RowEndTick - current.RowStartTick);
					if (eventTick > tick + 1e-9)
					{
						current.DueTick = eventTick;
						active.Add(current);
						continue;
					}
					PatternNoteProcessor.GenerateNotes(
						new ScriptEventSlice(scripted),
						current.Context, rowBuilder, out nominalDuration);
					current.ScriptEventIndex++;
				}
				else
				{
					PatternNoteProcessor.GenerateNotes(
						new RowSlice(current.Raw, current.Row),
						current.Context, rowBuilder, out nominalDuration);
				}

				foreach (NoteEvent item in rowBuilder.Freeze())
				{
					// On the supported subset, all source starts and tempo
					// changes happen at the row boundary; Dxy effect cleanup
					// is postponed until that cursor advances to its next row.
					if (item.Offset.TimeOffset != TimeSpan.Zero)
					{
						if (item.Offset.TimeOffset == nominalDuration
							&& item.Commands.All(command =>
								command is ClearNoteVolumeSlideCommand))
						{
							current.EndOfRowCommands.Add(item);
							continue;
						}
						throw new NotSupportedException(
							"Chronological data row cursor does not yet support delayed row commands.");
					}

					List<NoteCommand> retained = [];
					foreach (NoteCommand command in item.Commands)
					{
						if (command is not StartNoteCommand start
							|| start.Mixdown
							|| !document.TryGet(start.SourceId, out SongObject? childObject)
							|| childObject is not PatternDefinition child)
						{
							retained.Add(command);
							continue;
						}
						if (start.PitchMultiplier != 1.0
							|| start.PlaybackSpeedMultiplier != 1.0
							|| start.Volume.HasValue)
							throw new NotSupportedException(
								"Flattened child pitch/speed/volume transforms are not yet implemented by the chronological row scheduler.");
						if (item.Target.Kind != ChannelTargetKind.Physical)
							throw new NotSupportedException(
								"Flattened child must start on a physical channel.");
						int offset = item.Target.PhysicalChannel
							- current.Context.PhysicalChannelBase;
						active.Add(CreateCursor(
							start.SourceId, child,
							current.Context.FlattenedChild(
								physicalChannelOffset: offset),
							current.Ancestry));
					}

					if (retained.Count != 0)
						output.Append(item with
						{
							Commands = retained,
							Offset = new MusicalTime(elapsed, 0),
						});
				}

				if (current.Pattern is ScriptPatternDefinition)
				{
					if (current.ScriptEventIndex < current.ScriptRowEvents.Count)
					{
						NoteEvent nextEvent =
							current.ScriptRowEvents[current.ScriptEventIndex];
						current.DueTick = current.RowStartTick
							+ (ScriptEventDueRow(nextEvent) - current.Row)
								* (current.RowEndTick - current.RowStartTick);
					}
					else
						current.DueTick = current.RowEndTick;
				}
				else
				{
					current.Row++;
					// Speed is captured after the row's boundary effects.
					current.DueTick = tick + context.State.Speed;
				}
				active.Add(current);
			}
			tempo = context.State.Tempo;
		}
		return new SongScheduleCompilationResult(
			output.Freeze(), elapsed, [])
		{
			PlaybackPositions = positions,
		};
	}
}
