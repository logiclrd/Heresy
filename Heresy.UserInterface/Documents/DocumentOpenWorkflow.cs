using System;
using System.Threading.Tasks;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// File -> Open is picker-first: the selected path is not allowed to replace
/// the current document until the unsaved-change guard has approved it.
/// A canceled/unsupported picker never asks to save the current song.
/// New and Exit intentionally keep their original guard-first handling.
/// </summary>
public static class DocumentOpenWorkflow
{
	public static async Task<bool> TryOpenAsync(
		Func<Task<string?>> chooseFileAsync,
		Func<Task<bool>> confirmCanReplaceAsync,
		Action<string> openDocument)
	{
		ArgumentNullException.ThrowIfNull(chooseFileAsync);
		ArgumentNullException.ThrowIfNull(confirmCanReplaceAsync);
		ArgumentNullException.ThrowIfNull(openDocument);

		string? path = await chooseFileAsync();
		if (string.IsNullOrWhiteSpace(path))
			return false;

		if (!await confirmCanReplaceAsync())
			return false;

		openDocument(path);
		return true;
	}
}
