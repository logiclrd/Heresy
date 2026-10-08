using System;
using System.Threading.Tasks;

namespace Heresy.UserInterface.Documents;

public enum UnsavedChangesChoice
{
	Save,
	Discard,
	Cancel,
}

/// <summary>
/// Centralizes the dirty-document decision so window close, New and Open use
/// identical Yes/No/Cancel semantics.
/// </summary>
public static class UnsavedChangesGuard
{
	public static async Task<bool> CanProceedAsync(
		DocumentWorkspace workspace,
		Func<Task<UnsavedChangesChoice>> chooseAsync,
		Func<Task<bool>> saveAsync)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(chooseAsync);
		ArgumentNullException.ThrowIfNull(saveAsync);

		if (!workspace.IsModified)
			return true;

		UnsavedChangesChoice choice =
			await chooseAsync();

		return choice switch
		{
			UnsavedChangesChoice.Save =>
				await saveAsync(),
			UnsavedChangesChoice.Discard =>
				true,
			UnsavedChangesChoice.Cancel =>
				false,
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(choice)),
		};
	}
}
