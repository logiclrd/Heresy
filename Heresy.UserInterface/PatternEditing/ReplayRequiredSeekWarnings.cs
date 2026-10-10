using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>Advisory, never an instruction to skip a supported seek.</summary>
public sealed record ReplayRequiredSeekIndication(
	string Message, bool IsConditional);

/// <summary>
/// Conservative preview of native-frame seeks in displayed data Pattern
/// occurrences. The runtime sound's SeekCost is authoritative, especially
/// for pitch-dependent Instrument selection and scripted flow.
/// </summary>
public static class ReplayRequiredSeekWarnings
{
	private enum Cost { NoVoice, Direct, Replay, Possible }

	private sealed class Channel
	{
		public ObjectId Source;
		public bool UnknownSource;
		public Cost Current;
		public bool MayBeDifferent;
		public byte OffsetLow;
		public byte OffsetHigh;
		public byte Retrigger;
	}

	public static IReadOnlyDictionary<(int Row, int Channel),
		ReplayRequiredSeekIndication> Analyze(
			SongDocument document, PatternEditorContext context)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(context);
		Dictionary<(int Row, int Channel), ReplayRequiredSeekIndication> found = [];
		Channel[] channels = Enumerable.Range(0, context.MaxChannelCount)
			.Select(_ => new Channel { UnknownSource = context.IsSequence })
			.ToArray();

		foreach (PatternEditorSegment segment in context.Segments)
		{
			if (segment.Pattern is null)
			{
				foreach (Channel channel in channels)
				{
					channel.UnknownSource = true;
					channel.MayBeDifferent = true;
					channel.Current = Cost.Possible;
				}
				continue;
			}

			for (int display = segment.FirstDisplayRow;
				display < segment.FirstDisplayRow + segment.DisplayRowCount; display++)
			{
				PatternEditorRow row = context.GetRow(display);
				bool flowMayChangeOrder = false;
				for (int c = 0; c < row.Pattern.ChannelCount; c++)
				{
					PatternCell? cell = row.Pattern.Grid[row.PatternRow, c];
					if (cell is null)
						continue;
					Channel channel = channels[c];
					ObjectId selection = !cell.SourceId.IsNone
						? cell.SourceId
						: cell.Note is StartPatternNote note ? note.SourceId : ObjectId.None;
					if (!selection.IsNone)
					{
						channel.Source = selection;
						channel.UnknownSource = false;
					}
					if (cell.Note is StartPatternNote start)
					{
						channel.Current = channel.UnknownSource
							? Cost.Possible
							: Classify(document, channel.Source, start.Mixdown,
								new HashSet<ObjectId>());
						channel.MayBeDifferent = false;
					}
					else if (cell.Note is PatternNoteCut)
					{
						channel.Current = Cost.NoVoice;
						channel.MayBeDifferent = false;
					}

					bool offsetApplied = false, retriggerApplied = false;
					foreach (PatternEffect effect in cell.Effects)
					{
						switch (effect)
						{
							case SampleOffsetHighPatternEffect high:
								channel.OffsetHigh = high.HighOffset;
								break;
							case SampleOffsetPatternEffect offset:
								if (offset.Parameter != 0)
									channel.OffsetLow = offset.Parameter;
								// Oxx without a note start only changes memory.
								// O00 can recall an earlier nonzero Oxx/SAx.
								offsetApplied = cell.Note is StartPatternNote
									&& (channel.OffsetLow != 0 || channel.OffsetHigh != 0);
								break;
							case RetriggerPatternEffect retrigger:
								if (retrigger.Parameter != 0)
									channel.Retrigger = retrigger.Parameter;
								// Qx0 is not a periodic retrigger unless it
								// recalls an earlier nonzero tick countdown.
								retriggerApplied = (channel.Retrigger & 0x0F) != 0;
								break;
							case TrackerOrderJumpPatternEffect:
							case TrackerPatternBreakPatternEffect:
								flowMayChangeOrder = true;
								break;
						}
					}

					if ((offsetApplied || retriggerApplied)
						&& channel.Current is Cost.Replay or Cost.Possible)
					{
						bool conditional = channel.Current == Cost.Possible
							|| channel.MayBeDifferent;
						string effects = offsetApplied && retriggerApplied
							? "Oxx and Qxy" : offsetApplied ? "Oxx" : "Qxy";
						string lead = conditional ? "Possible expensive seek: "
							: "Expensive seek: ";
						found[(display, c)] = new(
							lead + effects + " may require replaying earlier source frames"
							+ " in realtime when the selected sound has ReplayRequired"
							+ " native-source seeking. Offline export remains exact."
							+ (conditional ? " The actual sound/flow is resolved at playback." : "")
							+ " This is only a performance hint; the effect remains editable.",
							conditional);
					}
				}
				if (flowMayChangeOrder)
					foreach (Channel channel in channels)
					{
						channel.UnknownSource = true;
						channel.MayBeDifferent = true;
						channel.Current = Cost.Possible;
					}
			}
		}
		return found;
	}

	private static Cost Classify(SongDocument document, ObjectId source,
		bool mixdown, HashSet<ObjectId> visited)
	{
		if (source.IsNone || !document.TryGet(source, out SongObject? entry))
			return Cost.NoVoice;
		if (entry is PatternDefinition or SequenceDefinition)
			return mixdown ? Cost.Replay : Cost.NoVoice;
		if (entry is not InstrumentDefinition instrument)
			return Cost.Direct;
		if (!visited.Add(source))
			return Cost.Possible; // Cyclic indirect tone selection cannot be proven.
		try
		{
			foreach (ToneSpecification tone in instrument.ToneSpecifications)
				if (Classify(document, tone.SourceId, true, visited)
					is Cost.Replay or Cost.Possible)
					return Cost.Possible;
			return Cost.Direct;
		}
		finally { visited.Remove(source); }
	}
}
