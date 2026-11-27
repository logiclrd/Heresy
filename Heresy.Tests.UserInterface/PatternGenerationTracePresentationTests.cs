using System;
using System.Linq;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.UserInterface.Views;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternGenerationTracePresentationTests
{
    private static PatternGenerationTracePresentation Model(string source)
    {
        var compiled = ScriptCompiler.CompilePatternGenerationTrace(
            new ScriptPatternDefinition((ObjectId)1U, "Projection")
            { Source = source, RowCount = 16, ChannelCount = 3 });
        Assert.That(compiled.Success, Is.True, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        return new(compiled.Program!.Capture());
    }

    private static long[] EventIds(PatternGenerationTracePresentation model)
        => model.Rows.Where(r => r.OccurrenceId.HasValue).Select(r => r.OccurrenceId!.Value).ToArray();

    [Test]
    public void ThreeOrdersRetainSameEventsStableTiesAndChannelHeaders()
    {
        var model = Model("""
            #order relaxed
            Note(4, 2, _O(17));
            Note(1, 0, _O(18));
            Tempo(1, 125);
            Cut(4, 0);
            Off(1, 2, timeOffsetSeconds: 0.25);
            """);
        Assert.That(model.Order, Is.EqualTo(PatternTraceOrder.OriginalOrder));
        Assert.That(EventIds(model), Is.EqualTo(new long[] { 0, 1, 2, 3, 4 }));
        var originalRows = model.Rows;
        model.SelectOccurrences(new long[] { 4, 0 }, primary: 4);
        var spans = model.SelectedSourceSpans.ToArray();
        model.SetOrder(PatternTraceOrder.PlaybackOrder);
        Assert.That(EventIds(model), Is.EqualTo(new long[] { 1, 2, 4, 0, 3 }));
        Assert.That(model.PrimaryRowIndex, Is.EqualTo(2));
        model.SetOrder(PatternTraceOrder.PlaybackOrderByChannel);
        Assert.That(EventIds(model), Is.EqualTo(new long[] { 2, 1, 3, 4, 0 }));
        Assert.That(model.Rows.Where(r => r.Kind == PatternTraceRowKind.ChannelHeader).Select(r => r.Text),
            Is.EqualTo(new[] { "Global controls", "Channel 0", "Channel 2" }));
        Assert.That(model.SelectedOccurrenceIds, Is.EqualTo(new long[] { 0, 4 }));
        Assert.That(model.PrimaryOccurrenceId, Is.EqualTo(4));
        Assert.That(model.Rows[model.PrimaryRowIndex].OccurrenceId, Is.EqualTo(4));
        Assert.That(model.SelectedSourceSpans, Is.EqualTo(spans));
        model.SetOrder(PatternTraceOrder.OriginalOrder);
        Assert.That(model.Rows, Is.SameAs(originalRows));
    }

    [Test]
    public void DroppedStrictEventsAndPartialFailuresRemainVisibleInEveryOrder()
    {
        var model = Model("Note(4, 0, _O(17)); Off(1, 0); Cut(17, 0);");
        Assert.That(model.Trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Failed));
        foreach (var order in Enum.GetValues<PatternTraceOrder>())
        {
            model.SetOrder(order);
            Assert.That(EventIds(model), Is.EquivalentTo(new long[] { 0, 1 }));
            var dropped = model.Rows.Single(r => r.OccurrenceId == 1);
            Assert.That(dropped.Dropped, Is.True);
            Assert.That(dropped.Text, Does.Contain("Dropped in strict mode"));
            var status = model.Rows.Last();
            Assert.That(status.Kind, Is.EqualTo(PatternTraceRowKind.Status));
            Assert.That(status.Text, Does.Contain("retained prefix only").And.Contain("no audio published"));
            Assert.That(status.SourceSpan, Is.EqualTo(model.Trace.FailureContext!.SourceSpan));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupOperationsHaveSelectableMembersAndLoopsOnlyInOriginalOrder(bool relaxed)
    {
        var model = Model((relaxed ? "#order relaxed\n" : "") + """
            for (int i = 0; i < 2; i++)
            {
                using var group = NewNoteGroup();
                group += NoteVirtual(i * 6, _O(17));
                group += NoteVirtual(i * 6, _O(18));
                group.Off(i * 6 + 1);
            }
            """);
        Assert.That(model.Trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), model.Trace.Detail);
        var headers = model.Rows.Where(r => r.Kind == PatternTraceRowKind.GroupOperation).ToArray();
        Assert.That(headers.Length, Is.EqualTo(2));
        Assert.That(headers.Select(h => h.Text).Distinct().Count(), Is.EqualTo(2));
        Assert.That(model.Rows.Count(r => r.Kind == PatternTraceRowKind.GroupCreation), Is.EqualTo(2));
        Assert.That(model.Rows.Count(r => r.Kind == PatternTraceRowKind.Transfer), Is.EqualTo(4));
        Assert.That(model.Rows.Any(r => r.Kind == PatternTraceRowKind.LoopMarker), Is.True);
        model.SelectGroupOperation(headers[0].InvocationId!.Value);
        Assert.That(model.SelectedOccurrenceIds, Is.EqualTo(new long[] { 2, 3 }));
        Assert.That(model.SelectedSourceSpans.Count, Is.EqualTo(1));
        model.SetOrder(PatternTraceOrder.PlaybackOrderByChannel);
        var channels = model.Rows.Where(r => r.Kind == PatternTraceRowKind.ChannelHeader).ToArray();
        Assert.That(channels.Length, Is.EqualTo(4));
        Assert.That(channels.Select(r => r.Text).Distinct().Count(), Is.EqualTo(4));
        Assert.That(channels.Select(r => r.Text), Is.All.StartsWith("Virtual channel — line"));
        Assert.That(channels.Select(r => r.Target!.Value.VirtualIdentity.Slot).Distinct().Count(), Is.EqualTo(2));
        Assert.That(model.SelectedOccurrenceIds, Is.EqualTo(new long[] { 2, 3 }));
        Assert.That(model.Rows.Any(r => r.Kind == PatternTraceRowKind.LoopMarker
            || r.Kind == PatternTraceRowKind.GroupOperation), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectGroupTransferFollowsStartInOriginalProjection(bool relaxed)
    {
        var model = Model((relaxed ? "#order relaxed\n" : "") + """
            using var group = NewNoteGroup();
            group += NoteVirtual(0, _O(17));
            group.Off(4);
            """);
        Assert.That(model.Trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), model.Trace.Detail);
        Assert.That(model.Rows.Select(r => r.Kind), Is.EqualTo(new[] {
            PatternTraceRowKind.GroupCreation, PatternTraceRowKind.Event,
            PatternTraceRowKind.Transfer, PatternTraceRowKind.GroupOperation,
            PatternTraceRowKind.Event, PatternTraceRowKind.Status }));
        model.SetOrder(PatternTraceOrder.PlaybackOrderByChannel);
        var header = model.Rows.Single(r => r.Kind == PatternTraceRowKind.ChannelHeader);
        Assert.That(header.SourceSpan, Is.EqualTo(model.Trace.GetVirtualOrigins().Single().CreationSpan));
    }

    [Test]
    public void NavigationUsesCanonicalSnapshotAndInnermostInvocationEvenIfUnexecuted()
    {
        const string source = "#order relaxed\r\nNote(0, 0, _O(17));\r\nif (false) Off(4, 0);\r\n";
        var model = Model(source);
        int note = source.IndexOf("Note(", StringComparison.Ordinal);
        int token = source.IndexOf("_O(", StringComparison.Ordinal);
        int off = source.IndexOf("Off(", StringComparison.Ordinal);
        Assert.That(model.Navigate(source, note + 1), Is.EqualTo(new PatternTraceNavigation(note, "Note(0, 0, _O(17))".Length)));
        Assert.That(model.Navigate(source, token + 1), Is.EqualTo(new PatternTraceNavigation(token, "_O(17)".Length)));
        Assert.That(model.Navigate(source, off + 2), Is.EqualTo(new PatternTraceNavigation(off, "Off(4, 0)".Length)));
        Assert.That(model.Navigate(source, 0), Is.EqualTo(new PatternTraceNavigation(0, 0)));
        Assert.That(model.Navigate(source, source.Length), Is.EqualTo(new PatternTraceNavigation(source.Length, 0)));
        Assert.That(model.Navigate(source + " ", note), Is.Null);
    }

    [Test]
    public void GlobalAndBroadcastTargetsNeverFabricateIndividualChannels()
    {
        var model = Model("""
            #order relaxed
            Tempo(0, 125);
            Speed(0, 6);
            OffVirtualInScope(4);
            CutAllVirtual(5);
            """);
        Assert.That(model.Trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), model.Trace.Detail);
        model.SetOrder(PatternTraceOrder.PlaybackOrderByChannel);
        Assert.That(model.Rows.Where(r => r.Kind == PatternTraceRowKind.ChannelHeader).Select(r => r.Text),
            Is.EqualTo(new[] { "Global controls", "Virtual channels in scope", "All virtual channels" }));
        Assert.That(EventIds(model).Length, Is.EqualTo(4));
    }

    [Test]
    public void InvalidSelectionCannotLeaveStalePrimary()
    {
        var model = Model("Note(0, 0, _O(17));");
        model.SelectOccurrences(new long[] { 0, 0, 123 }, 123);
        Assert.That(model.SelectedOccurrenceIds, Is.EqualTo(new long[] { 0 }));
        Assert.That(model.PrimaryOccurrenceId, Is.EqualTo(0));
        model.SelectOccurrences(new long[] { 123 });
        Assert.That(model.SelectedOccurrenceIds, Is.Empty);
        Assert.That(model.PrimaryOccurrenceId, Is.Null);
        Assert.That(model.PrimaryRowIndex, Is.EqualTo(-1));
        Assert.That(model.SelectedSourceSpans, Is.Empty);
    }
}
