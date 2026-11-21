using System;
using System.Threading.Tasks;

using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptSourceLeaveGuardTests
{
    private sealed class FakeScriptEditor : IScriptSourceEditor
    {
        public string ScriptSourceDisplayName => "A Script";
        public string Applied = "original";
        public string Draft = "original";
        public bool SaveSucceeds = true;
        public int SaveCalls;
        public int DiscardCalls;
        public bool HasUnsavedScriptSource =>
            !string.Equals(Draft, Applied, StringComparison.Ordinal);

        public bool TrySaveScriptSource()
        {
            SaveCalls++;
            if (!SaveSucceeds)
                return false;
            Applied = Draft;
            return true;
        }

        public void DiscardUnsavedScriptSource()
        {
            DiscardCalls++;
            Draft = Applied;
        }
    }

    [Test]
    public async Task CleanAndRevertedSourceDoNotPrompt()
    {
        FakeScriptEditor editor = new();
        bool prompted = false;
        bool proceed = await ScriptSourceLeaveGuard.CanProceedAsync(editor,
            () =>
            {
                prompted = true;
                return Task.FromResult(UnsavedChangesChoice.Cancel);
            });
        Assert.That(proceed, Is.True);
        Assert.That(prompted, Is.False);

        editor.Draft = "changed";
        Assert.That(editor.HasUnsavedScriptSource, Is.True);
        editor.Draft = "original";
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
    }

    [TestCase(UnsavedChangesChoice.Save, true, 1, 0)]
    [TestCase(UnsavedChangesChoice.Discard, true, 0, 1)]
    [TestCase(UnsavedChangesChoice.Cancel, false, 0, 0)]
    public async Task PendingSourceRequiresTheSelectedDecision(
        UnsavedChangesChoice choice, bool proceed, int saves, int discards)
    {
        FakeScriptEditor editor = new() { Draft = "pending" };
        bool actual = await ScriptSourceLeaveGuard.CanProceedAsync(
            editor, () => Task.FromResult(choice));
        Assert.That(actual, Is.EqualTo(proceed));
        Assert.That(editor.SaveCalls, Is.EqualTo(saves));
        Assert.That(editor.DiscardCalls, Is.EqualTo(discards));
        Assert.That(editor.HasUnsavedScriptSource, Is.EqualTo(!proceed));
        Assert.That(editor.Applied,
            Is.EqualTo(choice == UnsavedChangesChoice.Save ? "pending" : "original"));
    }

    [Test]
    public async Task FailedSourceSaveRetainsDraftAndBlocksNavigation()
    {
        FakeScriptEditor editor = new() { Draft = "pending", SaveSucceeds = false };
        bool actual = await ScriptSourceLeaveGuard.CanProceedAsync(
            editor, () => Task.FromResult(UnsavedChangesChoice.Save));
        Assert.That(actual, Is.False);
        Assert.That(editor.HasUnsavedScriptSource, Is.True);
        Assert.That(editor.Applied, Is.EqualTo("original"));
        Assert.That(editor.Draft, Is.EqualTo("pending"));
    }
}
