using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class UnsavedChangesGuardTests
{
	[Test]
	public async Task CleanWorkspaceProceedsWithoutPromptOrSave()
	{
		DocumentWorkspace workspace = new();
		bool prompted = false;
		bool saved = false;

		bool proceed =
			await UnsavedChangesGuard.CanProceedAsync(
				workspace,
				() =>
				{
					prompted = true;
					return Task.FromResult(
						UnsavedChangesChoice.Cancel);
				},
				() =>
				{
					saved = true;
					return Task.FromResult(true);
				});

		proceed.Should().BeTrue();
		prompted.Should().BeFalse();
		saved.Should().BeFalse();
	}

	[TestCase(UnsavedChangesChoice.Discard, true, false)]
	[TestCase(UnsavedChangesChoice.Cancel, false, false)]
	public async Task DirtyWorkspaceHonorsDiscardAndCancel(
		UnsavedChangesChoice choice,
		bool expectedProceed,
		bool expectedSave)
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		bool saved = false;

		bool proceed =
			await UnsavedChangesGuard.CanProceedAsync(
				workspace,
				() => Task.FromResult(choice),
				() =>
				{
					saved = true;
					return Task.FromResult(true);
				});

		proceed.Should().Be(expectedProceed);
		saved.Should().Be(expectedSave);
	}

	[TestCase(true, true)]
	[TestCase(false, false)]
	public async Task SaveChoiceProceedsOnlyWhenSaveSucceeds(
		bool saveResult,
		bool expectedProceed)
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		int saves = 0;

		bool proceed =
			await UnsavedChangesGuard.CanProceedAsync(
				workspace,
				() => Task.FromResult(
					UnsavedChangesChoice.Save),
				() =>
				{
					saves++;
					return Task.FromResult(saveResult);
				});

		proceed.Should().Be(expectedProceed);
		saves.Should().Be(1);
	}

	private static DocumentWorkspace DirtyWorkspace()
	{
		DocumentWorkspace workspace = new();
		workspace.Document.MarkChanged(
			affectsAudio: false);
		return workspace;
	}
}
