using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

[Flags]
public enum PatternEditMask
{
	None = 0,
	Note = 1 << 0,
	Source = 1 << 1,
	Volume = 1 << 2,
	Default = Note | Source | Volume,
}

public static class PatternEditMaskEditor
{
	public static PatternEditMask Toggle(
		PatternEditMask mask,
		PatternCellField field)
	{
		if (!TryToggle(mask, field, out PatternEditMask result))
			throw new ArgumentOutOfRangeException(nameof(field));

		return result;
	}

	public static bool TryToggle(
		PatternEditMask mask,
		PatternCellField field,
		out PatternEditMask result)
	{
		PatternEditMask fieldMask =
			field switch
			{
				PatternCellField.Note => PatternEditMask.Note,
				PatternCellField.Source => PatternEditMask.Source,
				PatternCellField.Volume => PatternEditMask.Volume,
				_ => PatternEditMask.None,
			};

		if (fieldMask == PatternEditMask.None)
		{
			result = mask;
			return false;
		}

		result = mask ^ fieldMask;
		return true;
	}

	public static string Describe(
		PatternEditMask mask)
	{
		List<string> fields = [];
		if ((mask & PatternEditMask.Note) != 0)
			fields.Add("Note");
		if ((mask & PatternEditMask.Source) != 0)
			fields.Add("Source");
		if ((mask & PatternEditMask.Volume) != 0)
			fields.Add("Volume");

		return fields.Count == 0
			? "None"
			: string.Join(" + ", fields);
	}
}
