using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class AudioDeviceQualificationCsvRecorderTests
{
    [Test]
    public void OptInRecorderCapturesAtomicProducerAndCallbackCountersAcrossSessions()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "heresy-c5e-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "device.csv");
        try
        {
            using BufferedAudioOutputSource producer = new(new ConstantSource(),
                capacityFrames: 32, blockFrames: 8, profileProducerRender: true);
            Assert.That(SpinWait.SpinUntil(
                () => producer.BufferedFrames == producer.CapacityFrames
                    || producer.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(producer.RenderingFault, Is.Null);

            AudioDeviceCallbackObservation callback = new(
                Calls: 4, RequestedFrames: 48, PeakRequestedFrames: 16,
                PeakElapsedTicks: Stopwatch.Frequency / 1000,
                CallsOverNominalRequestDuration: 1);

            using (AudioDeviceQualificationCsvRecorder first = new(
                path, producer, () => callback, () => null,
                sampleIntervalMilliseconds: 60_000))
            {
                first.SampleNow();
                float[] playback = new float[8];
                producer.Render(playback.Length, playback);
                Assert.That(playback, Is.All.EqualTo(0.25f));
                first.SampleNow();
            }
            using (AudioDeviceQualificationCsvRecorder second = new(
                path, producer, () => callback, () => null,
                sampleIntervalMilliseconds: 60_000))
                second.SampleNow();

            string[] rows = File.ReadAllLines(path);
            Assert.That(rows.Length, Is.EqualTo(7),
                "one header, two first-session samples and distinct start/stop rows per session");
            string[] names = rows[0].Split(',');
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Length));
            int Column(string name) => Array.IndexOf(names, name);
            Assert.Multiple(() =>
            {
                Assert.That(names[Column("utc")], Is.EqualTo("utc"));
                Assert.That(Column("worker_multi_allocated_bytes"),
                    Is.GreaterThan(0));
                Assert.That(Column("callback_peak_ms"), Is.GreaterThan(0));
                Assert.That(Column("fault_type"), Is.EqualTo(names.Length - 1));
                Assert.That(rows.Count(x => x == rows[0]), Is.EqualTo(1),
                    "reopening a qualification log must not repeat the header");
            });
            string[][] columns = rows.Skip(1).Select(x => x.Split(',')).ToArray();
            Assert.That(columns.All(x => x.Length == names.Length), Is.True,
                "each low-rate snapshot must retain a stable CSV schema");
            Assert.That(columns.Select(x => x[Column("record")]),
                Is.EqualTo(new[] { "start", "sample", "sample", "stop",
                    "start", "sample", "stop" }.Take(columns.Length)));
            Assert.That(columns[0][Column("session_id")],
                Is.Not.EqualTo(columns[4][Column("session_id")]));
            Assert.That(columns[0][Column("sample_rate")], Is.EqualTo("48000"));
            Assert.That(columns[0][Column("channels")], Is.EqualTo("1"));
            Assert.That(columns[0][Column("ring_capacity_frames")],
                Is.EqualTo("32"));
            Assert.That(columns[0][Column("producer_quantum_frames")],
                Is.EqualTo("8"));
            Assert.That(columns[1][Column("callback_calls")], Is.EqualTo("4"));
            Assert.That(columns[1][Column("callback_requested_frames")],
                Is.EqualTo("48"));
            Assert.That(columns[1][Column("callback_over_nominal_request_duration")],
                Is.EqualTo("1"));
            Assert.That(long.Parse(
                columns[1][Column("producer_blocks")], CultureInfo.InvariantCulture),
                Is.GreaterThan(0));
            Assert.That(double.Parse(
                columns[1][Column("producer_peak_ms")], CultureInfo.InvariantCulture),
                Is.GreaterThanOrEqualTo(0));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ConstantSource : IAudioOutputSource
    {
        public AudioOutputFormat Format { get; } = new(48000, 1);

        public void Render(int frameCount, Span<float> destination)
        {
            if (destination.Length != frameCount)
                throw new ArgumentException("Mono frames required");
            destination.Fill(0.25f);
        }
    }
}
