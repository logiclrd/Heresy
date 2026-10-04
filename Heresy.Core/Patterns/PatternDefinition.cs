using Heresy.Core.Objects;

namespace Heresy.Core.Patterns;

public abstract class PatternDefinition : SongObject
{
	protected PatternDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Pattern;

	public int RowCount { get; set; } = 64;
	public int ChannelCount { get; set; } = 8;

	public int MinorHighlightRows { get; set; } = 4;
	public int MajorHighlightRows { get; set; } = 16;
}
