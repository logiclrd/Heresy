using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DocumentOpenWorkflowTests
{
	[TestCase(null)]
	[TestCase("")]
	[TestCase("   ")]
	public async Task CanceledOrUnusablePickerDoesNotAskToSaveOrLoad(
		string? selectedPath)
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		var oldDocument = workspace.Document;
		List<string> steps = [];

		bool opened = await DocumentOpenWorkflow.TryOpenAsync(
			() => { steps.Add("pick"); return Task.FromResult(selectedPath); },
			() => { steps.Add("confirm"); return Task.FromResult(true); },
			_ => { steps.Add("open"); workspace.New(); });

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.False);
			Assert.That(steps, Is.EqualTo(new[] { "pick" }));
			Assert.That(workspace.Document, Is.SameAs(oldDocument));
			Assert.That(workspace.IsModified, Is.True);
		});
	}

	[Test]
	public async Task SelectedPathPromptsBeforeLoadingAndHonorsCancel()
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		var oldDocument = workspace.Document;
		List<string> steps = [];

		bool opened = await DocumentOpenWorkflow.TryOpenAsync(
			() => { steps.Add("pick"); return Task.FromResult<string?>("song.hm"); },
			() => { steps.Add("confirm"); return Task.FromResult(false); },
			_ => { steps.Add("open"); workspace.New(); });

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.False);
			Assert.That(steps, Is.EqualTo(new[] { "pick", "confirm" }));
			Assert.That(workspace.Document, Is.SameAs(oldDocument));
			Assert.That(workspace.IsModified, Is.True);
		});
	}

	[Test]
	public async Task SelectedPathWithApprovalLoadsAfterPickerAndConfirmation()
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		List<string> steps = [];

		bool opened = await DocumentOpenWorkflow.TryOpenAsync(
			() => { steps.Add("pick"); return Task.FromResult<string?>("song.hm"); },
			() => { steps.Add("confirm"); return Task.FromResult(true); },
			path =>
			{
				steps.Add("open:" + path);
				workspace.New();
			});

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.True);
			Assert.That(steps,
				Is.EqualTo(new[] { "pick", "confirm", "open:song.hm" }));
			Assert.That(workspace.IsModified, Is.False);
		});
	}

	[Test]
	public async Task RealGuardOnDirtyWorkspaceSkipsSaveOnCanceledPicker()
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		int prompts = 0;
		int saves = 0;
		var oldDocument = workspace.Document;

		bool opened = await DocumentOpenWorkflow.TryOpenAsync(
			() => Task.FromResult<string?>(null),
			() => UnsavedChangesGuard.CanProceedAsync(
				workspace,
				() =>
				{
					prompts++;
					return Task.FromResult(UnsavedChangesChoice.Save);
				},
				() =>
				{
					saves++;
					return Task.FromResult(true);
				}),
			_ => workspace.New());

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.False);
			Assert.That(prompts, Is.Zero);
			Assert.That(saves, Is.Zero);
			Assert.That(workspace.Document, Is.SameAs(oldDocument));
		});
	}

	[Test]
	public async Task FailedSaveDoesNotReplaceExistingDocument()
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		var oldDocument = workspace.Document;
		int loaded = 0;

		bool opened = await DocumentOpenWorkflow.TryOpenAsync(
			() => Task.FromResult<string?>("another.hm.json"),
			() => UnsavedChangesGuard.CanProceedAsync(
				workspace,
				() => Task.FromResult(UnsavedChangesChoice.Save),
				() => Task.FromResult(false)),
			_ => { loaded++; workspace.New(); });

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.False);
			Assert.That(loaded, Is.Zero);
			Assert.That(workspace.Document, Is.SameAs(oldDocument));
			Assert.That(workspace.IsModified, Is.True);
		});
	}

	[Test]
	public async Task FailedDocumentLoadDoesNotReplaceExistingWorkspace()
	{
		DocumentWorkspace workspace = DirtyWorkspace();
		var oldDocument = workspace.Document;
		string missing = Path.Combine(
			Path.GetTempPath(), $"heresy-open-missing-{Guid.NewGuid():N}.hm.json");

		Assert.ThrowsAsync<FileNotFoundException>(async () =>
		{
			await DocumentOpenWorkflow.TryOpenAsync(
				() => Task.FromResult<string?>(missing),
				() => Task.FromResult(true),
				workspace.Open);
		});

		Assert.Multiple(() =>
		{
			Assert.That(workspace.Document, Is.SameAs(oldDocument));
			Assert.That(workspace.FilePath, Is.Null);
			Assert.That(workspace.IsModified, Is.True);
		});
	}

	private static DocumentWorkspace DirtyWorkspace()
	{
		DocumentWorkspace workspace = new();
		workspace.Document.MarkChanged(affectsAudio: false);
		return workspace;
	}
}
