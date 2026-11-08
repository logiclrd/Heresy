using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSkipRealtimeEventOrderingTests
{
    [AvaloniaTest]
    public async Task KeydownAdvancesBeforeLiveAuditionCompletesAndOldRowTextInputCannotUndoIt()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Skip Test", rowCount: 8, channelCount: 1);
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new SampleDefinition(
            sampleId, "Source", new ExternalAssetReference(
                Path.GetFullPath("skip-acceptance-not-loaded.wav"))));
        TaskCompletionSource playback = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool auditionReached = false;
        PatternEditorControl editor = new(
            new Window(), workspace, pattern, () => { }, _ => { },
            liveAudition: new PatternLiveAuditionActions(_ =>
            {
                auditionReached = true;
                return playback.Task;
            }));

        PatternNoteInputState input = Field<PatternNoteInputState>(
            editor, "_noteInputState");
        input.SourceId = sampleId;
        input.SkipRows.Should().Be(1);
        PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
        cursor.Row.Should().Be(0);

        KeyEventArgs keyDown = new()
        {
            Key = Key.Z,
            PhysicalKey = PhysicalKey.Z,
            KeyModifiers = KeyModifiers.None,
        };
        Task action = InvokeAsync(editor, "OnCellKeyDownAsync", 0, 0, keyDown);
        auditionReached.Should().BeTrue(
            "the test must exercise the incomplete realtime playback await");
        action.IsCompleted.Should().BeFalse();
        pattern.Grid[0, 0]!.Note.Should().BeOfType<StartPatternNote>();
        cursor.Row.Should().Be(1, "Skip is applied synchronously at note entry");
        keyDown.Handled.Should().BeTrue(
            "the key event must be consumed before the live-audio await");

        // Avalonia may deliver the old source cell's TextInput before the
        // asynchronous audio acknowledgement completes.
        TextInputEventArgs trailingText = new() { Text = "z" };
        Invoke(editor, "OnCellTextInput", 0, 0, trailingText);
        trailingText.Handled.Should().BeTrue();
        cursor.Row.Should().Be(1,
            "trailing text for the old row must never restore the cursor");

        playback.SetResult();
        await action;
        cursor.Row.Should().Be(1);
        cursor.Field.Should().Be(PatternCellField.Note);
    }

    [AvaloniaTest]
    public async Task SkipThreeAndNoAudioHostStillAdvanceExactlyThreeRows()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Skip Test", rowCount: 8, channelCount: 1);
        PatternEditorControl editor = new(
            new Window(), workspace, pattern, () => { }, _ => { });
        Field<PatternNoteInputState>(editor, "_noteInputState").SkipRows = 3;
        PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");

        KeyEventArgs keyDown = new()
        {
            Key = Key.Z,
            PhysicalKey = PhysicalKey.Z,
            KeyModifiers = KeyModifiers.None,
        };
        await InvokeAsync(editor, "OnCellKeyDownAsync", 0, 0, keyDown);
        keyDown.Handled.Should().BeTrue();
        cursor.Row.Should().Be(3);
        cursor.Field.Should().Be(PatternCellField.Note);

        TextInputEventArgs trailingText = new() { Text = "z" };
        Invoke(editor, "OnCellTextInput", 0, 0, trailingText);
        trailingText.Handled.Should().BeTrue();
        cursor.Row.Should().Be(3);
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(
            name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target)!;

    private static Task InvokeAsync(object target, string name,
        params object[] args)
        => (Task)target.GetType().GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, args)!;

    private static void Invoke(object target, string name,
        params object[] args)
        => target.GetType().GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, args);
}
