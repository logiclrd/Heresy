using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Patterns;

public abstract class PatternDefinition : SongObject
{
	private int _rowCount = 64;
	private int _channelCount = 8;

	protected PatternDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Pattern;

	public int RowCount
	{
		get => _rowCount;
		set
		{
			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value));

			if (_rowCount == value)
				return;

			int previousRowCount = _rowCount;
			int previousChannelCount = _channelCount;
			_rowCount = value;
			OnDimensionsChanged(previousRowCount, previousChannelCount);
		}
	}

	public int ChannelCount
	{
		get => _channelCount;
		set
		{
			if (value <= 0)
				throw new ArgumentOutOfRangeException(nameof(value));

			if (_channelCount == value)
				return;

			int previousRowCount = _rowCount;
			int previousChannelCount = _channelCount;
			_channelCount = value;
			OnDimensionsChanged(previousRowCount, previousChannelCount);
		}
	}

	public int MinorHighlightRows { get; set; } = 4;
	public int MajorHighlightRows { get; set; } = 16;

	protected virtual void OnDimensionsChanged(int previousRowCount, int previousChannelCount) { }
}
