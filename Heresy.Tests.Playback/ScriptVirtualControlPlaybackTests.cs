using System;
using System.IO;
using System.Numerics;
using System.Text;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>Production Roslyn scripts exercising real scoped voice state.</summary>
[TestFixture]
public sealed class ScriptVirtualControlPlaybackTests
{
    [Test]
    public void VolumeNnaAndPastCutAffectOnlyDisplacedVoiceOfTargetVirtualId()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        ObjectId patternId = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            using var v8 = NoteVirtual(0, _O({sample.Value}));
            v7.Volume(0.25, 0.25);
            v7.Displacement(0.5, NoteDisplacementAction.Continue);
            v7.Note(0.75, _O({sample.Value}));
            v7.Past(1, TrackerPastNoteAction.Cut);
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan one = factory.Create(document, patternId);
        using PreparedIncrementalPlaybackPlan many = factory.Create(document, patternId);
        float[] expected = new float[200];
        one.Source.Render(110, expected.AsSpan(0, 110));
        Assert.That(one.Session.VirtualVoices.Count, Is.EqualTo(1),
            "The old voice must migrate when a new virtual note displaces it.");
        one.Source.Render(90, expected.AsSpan(110, 90));
        Assert.That(one.Session.VirtualVoices, Is.Empty,
            "PastVirtual(Cut) removes displaced NNA voices, not the current one.");
        float[] chunks = new float[200];
        int[] sizes = [1, 7, 19, 3, 61];
        for (int pos = 0, i = 0; pos < chunks.Length; i++)
        {
            int n = Math.Min(chunks.Length - pos, sizes[i % sizes.Length]);
            many.Source.Render(n, chunks.AsSpan(pos, n));
            pos += n;
        }
        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(expected[12], Is.GreaterThan(0.9f),
                "Both initial voices sound.");
            Assert.That(expected[50], Is.GreaterThan(0.52f));
            Assert.That(expected[50], Is.LessThan(expected[12] * 0.8f),
                "VolumeVirtual reduces only virtual ID 7.");
            Assert.That(expected[105], Is.GreaterThan(expected[50] + 0.08f),
                "The displaced Continue voice remains audible.");
            Assert.That(expected[155], Is.LessThan(expected[105] - 0.08f),
                "PastVirtual cuts the displaced voice without cutting its replacement.");
            Assert.That(expected[155], Is.GreaterThan(0.5f),
                "The replacement and unrelated virtual ID 8 survive.");
        });
    }

    [Test]
    public void NativeFrameSeekAtDeferredWallDeadlineTargetsReplacementVoice()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: true);
        ObjectId withOffset = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.SourceFrame(0.5, 300, timeOffsetSeconds: 0.04);
            v7.Note(0.75, _O({sample.Value}));
            using var v8 = NoteVirtual(0.75, _O({sample.Value}));
            """);
        ObjectId noOffset = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.Note(0.75, _O({sample.Value}));
            using var v8 = NoteVirtual(0.75, _O({sample.Value}));
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan seek = factory.Create(document, withOffset);
        using PreparedIncrementalPlaybackPlan baseline = factory.Create(document, noOffset);
        float[] a = new float[135];
        float[] b = new float[135];
        seek.Source.Render(a.Length, a);
        baseline.Source.Render(b.Length, b);
        Assert.Multiple(() =>
        {
            Assert.That(a[80], Is.EqualTo(b[80]).Within(1e-5),
                "The native source-frame offset is not applied at raw row 0.5.");
            Assert.That(a[95], Is.EqualTo(b[95]).Within(1e-5),
                "The replacement voice starts before the deferred wall-time deadline.");
            Assert.That(a[114], Is.GreaterThan(b[114] + 0.3f),
                "Only virtual ID 7's replacement jumps to source frame 300.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PhysicalControlsMatchOwnedVoiceAndDeferredSeekTargetsReplacement(bool relaxed)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: true);
        string prefix = relaxed ? "#order relaxed\n" : "";
        string lookup = $"_O({sample.Value})";
        ObjectId physical = AddPattern(document, prefix + $$"""
            Note(0, 0, {{lookup}});
            using var sibling = NoteVirtual(0, {{lookup}});
            Volume(0.25, 0, 0.5);
            Displacement(0.5, 0, NoteDisplacementAction.Continue);
            SourceFrame(0.5, 0, 300, timeOffsetSeconds: 0.04);
            Note(0.75, 0, {{lookup}});
            Past(1.25, 0, TrackerPastNoteAction.Cut);
            Volume(1.5, 0, 0.25);
            Cut(3, 0);
            """);
        ObjectId virtualPattern = AddPattern(document, prefix + $$"""
            using var note = NoteVirtual(0, {{lookup}});
            using var sibling = NoteVirtual(0, {{lookup}});
            note.Volume(0.25, 0.5);
            note.Displacement(0.5, NoteDisplacementAction.Continue);
            note.SourceFrame(0.5, 300, timeOffsetSeconds: 0.04);
            note.Note(0.75, {{lookup}});
            note.Past(1.25, TrackerPastNoteAction.Cut);
            note.Volume(1.5, 0.25);
            note.Cut(3);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var reference = factory.Create(document, virtualPattern);
        using var actual = factory.Create(document, physical);
        float[] expected = new float[480], pcm = new float[480];
        reference.Source.Render(expected.Length, expected);
        actual.Source.Render(130, pcm.AsSpan(0, 130));
        Assert.That(actual.Session.VirtualVoices.Count, Is.EqualTo(1));
        for (int pos = 130; pos < pcm.Length;)
        {
            int count = Math.Min(17, pcm.Length - pos);
            actual.Source.Render(count, pcm.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(pcm, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(pcm[114], Is.GreaterThan(pcm[95] + 0.15f),
                "Deferred native seek affects the replacement voice.");
            Assert.That(pcm[175], Is.LessThan(pcm[140] - 0.04f),
                "Past cuts the old voice but leaves the current one and sibling.");
            Assert.That(pcm[390], Is.EqualTo(16000f / 32768f).Within(1e-6f));
            Assert.That(actual.Session.VirtualVoices, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PhysicalVolumeAndDisplacementControlFlattenedLogicalNote(bool relaxed)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        string lookup = $"_O({sample.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        ObjectId child = document.AllocateObjectId();
        document.Add(new ScriptPatternDefinition(child, "Flattened child")
        {
            RowCount = 4, ChannelCount = 2, Source = prefix + $$"""
                Note(0, 0, {{lookup}});
                using var guest = NoteVirtual(0, {{lookup}});
                Note(2, 1, {{lookup}});
                """
        });
        ObjectId root = AddPattern(document, prefix + $$"""
            Note(0, 0, _O({{child.Value}}));
            using var sibling = NoteVirtual(0, {{lookup}});
            Volume(0.5, 0, 0.25);
            Displacement(0.75, 0, NoteDisplacementAction.Continue);
            Note(1, 0, {{lookup}}, volume: 0.5);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var plan = factory.Create(document, root);
        using var chunked = factory.Create(document, root);
        float[] pcm = new float[360], chunks = new float[360];
        plan.Source.Render(pcm.Length, pcm);
        for (int pos = 0; pos < chunks.Length;)
        {
            int count = Math.Min(19, chunks.Length - pos);
            chunked.Source.Render(count, chunks.AsSpan(pos, count));
            pos += count;
        }
        const float amplitude = 16000f / 32768f;
        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.EqualTo(pcm).Within(1e-6f));
            Assert.That(pcm[30], Is.EqualTo(3 * amplitude).Within(1e-6f));
            Assert.That(pcm[90], Is.EqualTo(1.5f * amplitude).Within(1e-6f),
                "Logical gain reaches physical and virtual descendants once, not the sibling.");
            Assert.That(pcm[150], Is.EqualTo(2 * amplitude).Within(1e-6f),
                "The displaced flattened invocation continues beside its replacement.");
            Assert.That(pcm[270], Is.EqualTo(2.25f * amplitude).Within(1e-6f),
                "Continue permits the old producer's later note.");
        });
    }

    [Test]
    public void DeferredVirtualControlOfCanceledCursorCannotTouchSiblingId()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        ObjectId first = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.Volume(0.5, 0.05, timeOffsetSeconds: 0.08);
            """);
        ObjectId second = AddPattern(document,
            $"using var v7 = NoteVirtual(0, _O({sample.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(document, first);
        long sibling = plan.Timeline.AddRoot(second);
        float[] before = new float[30];
        plan.Source.Render(before.Length, before);
        Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);
        Assert.That(plan.Timeline.IsInvocationActive(sibling), Is.True);
        Assert.That(plan.Session.RetainedScopedVirtualMemoryCount, Is.LessThanOrEqualTo(1),
            "Cancellation retires the abandoned generation's memory even while audio tails remain.");
        float[] after = new float[160];
        plan.Source.Render(after.Length, after);
        Assert.Multiple(() =>
        {
            Assert.That(after[145], Is.GreaterThan(0.42f),
                "The canceled deferred volume command cannot reach another cursor.");
            Assert.That(after[145], Is.LessThan(before[10] * 0.7f),
                "The canceled cursor's own voice is gone.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupControlsMatchExplicitHandleControlsWithoutTouchingUnrelatedVoice(bool relaxed)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        string lookup = $"_O({sample.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        ObjectId groupPattern = AddPattern(document, prefix + $$"""
            using var other = NoteVirtual(0, {{lookup}});
            using (var chord = NewNoteGroup())
            {
                chord += NoteVirtual(0, {{lookup}});
                chord += NoteVirtual(0, {{lookup}});
                chord.Volume(0.5, 0.25);
                chord.Cut(1, timeOffsetSeconds: 0.02);
            }
            using var replacement = NoteVirtual(1.1, {{lookup}});
            replacement.Cut(2);
            """);
        ObjectId individualPattern = AddPattern(document, prefix + $$"""
            using var other = NoteVirtual(0, {{lookup}});
            using (var first = NoteVirtual(0, {{lookup}}))
            using (var second = NoteVirtual(0, {{lookup}}))
            {
                first.Volume(0.5, 0.25);
                second.Volume(0.5, 0.25);
                first.Cut(1, timeOffsetSeconds: 0.02);
                second.Cut(1, timeOffsetSeconds: 0.02);
            }
            using var replacement = NoteVirtual(1.1, {{lookup}});
            replacement.Cut(2);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var reference = factory.Create(document, individualPattern);
        using var group = factory.Create(document, groupPattern);
        float[] expected = new float[400], actual = new float[400];
        reference.Source.Render(expected.Length, expected);
        for (int pos = 0; pos < actual.Length;)
        {
            int count = Math.Min(17, actual.Length - pos);
            group.Source.Render(count, actual.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(actual[25], Is.GreaterThan(1.4f), "All three voices start.");
            Assert.That(actual[90], Is.EqualTo(16000f / 32768f * 1.5f).Within(1e-5f));
            Assert.That(actual[200], Is.EqualTo(16000f / 32768f * 2).Within(1e-5f),
                "The old group's deferred cuts leave the new generation and unrelated voice alive.");
            Assert.That(actual[320], Is.EqualTo(16000f / 32768f).Within(1e-5f));
            Assert.That(group.SequencingContext.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void VirtualSpatialAndFilterMemoryMatchesPhysicalAcrossSilentGap(bool relaxed, bool privateSource)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: true);
        ObjectId source = privateSource ? AddPattern(document, $"Note(0, 0, _O({sample.Value}));") : sample;
        string lookup = $"_O({source.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        string mix = privateSource ? ", mixdown: true" : "";
        // Physical channels retain their state throughout silence. The
        // generated virtual channel must reconstruct it after eviction.
        ObjectId physical = AddPattern(document, prefix + $$"""
            Note(0, 0, {{lookup}}{{mix}});
            Pan(0, 0, -0.7, 0.2);
            Filter(0, 0, 0.4, 0.2);
            Surround(0.25, 0, true);
            Cut(0.5, 0);
            FilterCutoff(1, 0, 0.65);
            Pan(1, 0, 0.65, -0.1, 0.3);
            FilterResonance(1.25, 0, 0.1);
            Seek(1.25, 0, 0.2);
            Note(2, 0, {{lookup}}{{mix}});
            Seek(2, 0, 0.25, timeOffsetSeconds: 0.02);
            Surround(2.5, 0, true);
            Surround(2.75, 0, false);
            Pan(3, 0, -0.5);
            Cut(3.5, 0);
            """);
        ObjectId virtualId = AddPattern(document, prefix + $$"""
            using var note = NoteVirtual(0, {{lookup}});
            note.Pan(0, -0.7, 0.2);
            note.Filter(0, 0.4, 0.2);
            note.Surround(0.25, true);
            note.Cut(0.5);
            note.FilterCutoff(1, 0.65);
            note.Pan(1, 0.65, -0.1, 0.3);
            note.FilterResonance(1.25, 0.1);
            note.Seek(1.25, 0.2);
            note.Note(2, {{lookup}});
            note.Seek(2, 0.25, timeOffsetSeconds: 0.02);
            note.Surround(2.5, true);
            note.Surround(2.75, false);
            note.Pan(3, -0.5);
            note.Cut(3.5);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(new RenderConfiguration(1000,
            [new OutputChannelConfiguration(new Vector3(-1, 0, 0)),
             new OutputChannelConfiguration(new Vector3(1, 0, 0))]));
        using var reference = factory.Create(document, physical);
        using var actual = factory.Create(document, virtualId);
        using var chunks = factory.Create(document, virtualId);
        float[] expected = new float[960], rendered = new float[960], chunked = new float[960];
        reference.Source.Render(480, expected);
        actual.Source.Render(180, rendered.AsSpan(0, 360));
        Assert.That(actual.Session.RetainedScopedVirtualChannelCount, Is.Zero);
        Assert.That(actual.Session.RetainedScopedVirtualMemoryCount, Is.EqualTo(1));
        actual.Source.Render(300, rendered.AsSpan(360));
        for (int pos = 0; pos < 480;)
        {
            int count = Math.Min(17, 480 - pos);
            chunks.Source.Render(count, chunked.AsSpan(2 * pos, 2 * count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(chunked, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(Math.Abs(expected[540]), Is.GreaterThan(1e-4f),
                "The retrigger must actually produce audio.");
            if (privateSource)
                Assert.That(expected[540], Is.EqualTo(expected[541]).Within(1e-6f),
                    "Private mixdown preserves its child's existing speaker feeds, not outer pan.");
            else
                Assert.That(Math.Abs(expected[540] - expected[541]), Is.GreaterThan(1e-4f),
                    "The remembered off-center position is audible on a direct retrigger.");
            Assert.That(expected[640], Is.EqualTo(-expected[641]).Within(1e-5f),
                "Outer surround phase encoding still applies to centered stereo, including private mixdown.");
            Assert.That(actual.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
            Assert.That(chunks.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReverseParityGroupMatchesExplicitControlsAndPreservesDisplacedVoices(bool relaxed)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: true);
        string lookup = $"_O({sample.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        string starts = $$"""
            using var other = NoteVirtual(0, {{lookup}});
            using var a = NoteVirtual(0, {{lookup}});
            using var b = NoteVirtual(0, {{lookup}});
            a.Pan(0, -0.5);
            a.Displacement(0, NoteDisplacementAction.Continue);
            a.Note(0.25, {{lookup}});
            """;
        string[] controls =
        [
            "Seek(0.5, 0.3, timeOffsetSeconds: 0.02)", "Pan(0.75, 0.75)",
            "Filter(1, 0.4, 0.2)", "FilterCutoff(1.25, 0.65)",
            "FilterResonance(1.5, 0.1)", "Surround(1.75, true)",
            "Surround(2, false)", "Past(2.25, TrackerPastNoteAction.Cut)", "Cut(3)",
        ];
        ObjectId groupId = AddPattern(document, prefix + starts + "\n" +
            "using var group = NewNoteGroup(); group += a; group += b;\n"
            + string.Join(";\n", Array.ConvertAll(controls, call => "group." + call)) + ";");
        ObjectId explicitId = AddPattern(document, prefix + starts + "\n"
            + string.Join(";\n", Array.ConvertAll(controls, call => "a." + call + "; b." + call)) + ";");
        var factory = new PreparedIncrementalPlaybackFactory(new RenderConfiguration(1000,
            [new OutputChannelConfiguration(new Vector3(-1, 0, 0)),
             new OutputChannelConfiguration(new Vector3(1, 0, 0))]));
        using var reference = factory.Create(document, explicitId);
        using var actual = factory.Create(document, groupId);
        float[] expected = new float[960], rendered = new float[960];
        reference.Source.Render(480, expected);
        for (int pos = 0; pos < 480;)
        {
            int count = Math.Min(19, 480 - pos);
            actual.Source.Render(count, rendered.AsSpan(pos * 2, count * 2));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(actual.Session.VirtualVoices, Is.Empty);
            Assert.That(actual.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
            Assert.That(actual.SequencingContext.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void VirtualVolumeSurvivesSilentGap(bool relaxed, bool changeWhileSilent)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        string lookup = $"_O({sample.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        string idleChange = changeWhileSilent ? "note.Volume(1, 0.125);\n" : "";
        double remembered = changeWhileSilent ? 0.125 : 0.25;
        ObjectId actualId = AddPattern(document, prefix + $$"""
            using var note = NoteVirtual(0, {{lookup}}, volume: 0.25);
            note.Cut(0.5);
            {{idleChange}}
            note.Note(2, {{lookup}});
            note.Cut(3);
            """);
        // Explicit start volume is the independent musical reference.
        ObjectId referenceId = AddPattern(document, prefix + $$"""
            using var note = NoteVirtual(0, {{lookup}}, volume: 0.25);
            note.Cut(0.5);
            note.Note(2, {{lookup}}, volume: {{(changeWhileSilent ? "0.125" : "0.25")}});
            note.Cut(3);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var reference = factory.Create(document, referenceId);
        using var actual = factory.Create(document, actualId);
        using var chunks = factory.Create(document, actualId);
        float[] expected = new float[480], rendered = new float[480], chunked = new float[480];
        reference.Source.Render(expected.Length, expected);
        actual.Source.Render(180, rendered.AsSpan(0, 180));
        Assert.That(actual.Session.RetainedScopedVirtualChannelCount, Is.Zero,
            "No silent PCM channel should remain active before the next note.");
        Assert.That(actual.Session.RetainedScopedVirtualMemoryCount, Is.EqualTo(1),
            "The referenceable generation still remembers its volume.");
        actual.Source.Render(300, rendered.AsSpan(180));
        for (int pos = 0; pos < chunked.Length;)
        {
            int count = Math.Min(17, chunked.Length - pos);
            chunks.Source.Render(count, chunked.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(expected[180], Is.Zero.Within(1e-6f));
            Assert.That(expected[270], Is.EqualTo(16000f / 32768f * remembered).Within(1e-6));
            Assert.That(rendered, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(chunked, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(actual.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
            Assert.That(chunks.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OverlappingRecycledGroupsReclaimRenderStateDuringLongRunningPattern(bool relaxed)
    {
        const int cycles = 1024, framesPerRow = 120;
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        ObjectId filter = document.AllocateObjectId();
        document.Add(new FilterDefinition(filter, "Finite echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]));
        string source = $$"""
            for (int row = 0; row < {{cycles}}; row++)
            {
                using var group = NewNoteGroup();
                using var lead = NoteVirtual(row, _O({{sample.Value}}));
                lead.Displacement(row, NoteDisplacementAction.Continue);
                group += NoteVirtual(row, _O({{sample.Value}}));
                lead.Note(row + 0.25, _O({{sample.Value}}));
                group += lead;
                group.ChannelFilter(row + 0.25, _O({{filter.Value}}));
                group.Volume(row + 0.5, 0.25);
                group.Pan(row + 0.5, 0.7, -0.2);
                group.Filter(row + 0.5, 0.55, 0.2);
                group.Surround(row + 0.75, true, timeOffsetSeconds: 0.05);
                group.FilterCutoff(row + 0.75, 0.7, timeOffsetSeconds: 0.05);
                group.FilterResonance(row + 0.75, 0.1, timeOffsetSeconds: 0.05);
                group.Seek(row + 0.75, 0.2, timeOffsetSeconds: 0.05);
                group.Past(row + 0.75, TrackerPastNoteAction.Cut, timeOffsetSeconds: 0.06);
                group.Cut(row + 0.75, timeOffsetSeconds: 0.06);
            }
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        ObjectId patternId = document.AllocateObjectId();
        document.Add(new ScriptPatternDefinition(patternId, "Long ownership stress")
        {
            RowCount = cycles + 3, ChannelCount = 1, Source = source,
        });
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var reference = factory.Create(document, patternId);
        using var stream = factory.Create(document, patternId);
        int totalFrames = (cycles + 3) * framesPerRow;
        float[] expected = new float[totalFrames], actual = new float[totalFrames];
        reference.Source.Render(totalFrames, expected);
        int peakChannels = 0, peakFilters = 0, peakRetiring = 0, peakDisplaced = 0, peakMemory = 0;
        int[] chunks = [1, 37, 83, 19, 113];
        for (int pos = 0, index = 0; pos < actual.Length;)
        {
            int count = Math.Min(chunks[index++ % chunks.Length], actual.Length - pos);
            stream.Source.Render(count, actual.AsSpan(pos, count));
            pos += count;
            peakChannels = Math.Max(peakChannels, stream.Session.RetainedScopedVirtualChannelCount);
            peakMemory = Math.Max(peakMemory, stream.Session.RetainedScopedVirtualMemoryCount);
            peakFilters = Math.Max(peakFilters, stream.Session.RetainedScopedVirtualFilterCount);
            peakRetiring = Math.Max(peakRetiring, stream.Session.RetiringVirtualFilterCount);
            peakDisplaced = Math.Max(peakDisplaced, stream.Session.VirtualVoices.Count);
        }
        TestContext.Out.WriteLine($"Peak retained channels={peakChannels}, filters={peakFilters}, retiring={peakRetiring}, displaced={peakDisplaced}, memory={peakMemory}");
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(actual[50], Is.GreaterThan(0.5f), "The stress test must actually render voices.");
            Assert.That(peakChannels, Is.InRange(3, 12));
            Assert.That(peakMemory, Is.InRange(1, 12));
            Assert.That(stream.Session.RetainedScopedVirtualMemoryCount, Is.Zero);
            Assert.That(peakFilters, Is.InRange(2, 12));
            Assert.That(peakRetiring, Is.LessThanOrEqualTo(12));
            Assert.That(peakDisplaced, Is.InRange(1, 6));
            Assert.That(stream.Session.RetainedScopedVirtualChannelCount, Is.Zero);
            Assert.That(stream.Session.RetainedScopedVirtualFilterCount, Is.Zero);
            Assert.That(stream.Session.RetiringVirtualFilterCount, Is.Zero);
            Assert.That(stream.Session.VirtualVoices, Is.Empty);
            Assert.That(stream.SequencingContext.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
            Assert.That(stream.SequencingContext.Diagnostics.RejectedChannelFilterCommands, Is.Zero);
            Assert.That(actual[^1], Is.Zero.Within(1e-6f));
        });
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(
            Vector3.Zero, positionalImportance: 0)]);

    private static ObjectId AddPattern(SongDocument doc, string code)
    {
        ObjectId id = doc.AllocateObjectId();
        doc.Add(new ScriptPatternDefinition(id, "Virtual controls")
        {
            RowCount = 4, ChannelCount = 1, Source = code,
        });
        return id;
    }

    private static ObjectId AddSample(SongDocument doc, bool stepped)
    {
        ObjectId id = doc.AllocateObjectId();
        doc.Add(SampleDefinition.CreateImported(
            id, "Source", "virtual-control.wav", Wave(stepped)));
        return id;
    }

    private static byte[] Wave(bool stepped)
    {
        const int frames = 1200;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(1000);
        writer.Write(2000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
            writer.Write((short)(stepped && i < 200 ? 4000 : 16000));
        writer.Flush();
        return stream.ToArray();
    }
}
