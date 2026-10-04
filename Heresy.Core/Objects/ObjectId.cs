using System;
using System.Globalization;

namespace Heresy.Core.Objects;

/// <summary>
/// Stable identifier for an object within a Heresy song. IDs are monotonically
/// allocated and are never reused within the lifetime of a song.
/// </summary>
public readonly record struct ObjectId(uint Value) : IComparable<ObjectId>
{
	public static readonly ObjectId None = new(0);

	public bool IsNone => Value == 0;

	public int CompareTo(ObjectId other) => Value.CompareTo(other.Value);

	public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

	public static explicit operator uint(ObjectId id) => id.Value;
	public static explicit operator ObjectId(uint value) => new(value);
}
