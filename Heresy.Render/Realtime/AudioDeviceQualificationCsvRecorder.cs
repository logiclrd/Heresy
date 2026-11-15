using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Heresy.Render.Realtime;

/// <summary>
/// Optional, low-rate, off-audio-thread C5(e) diagnostics. All observations
/// are snapshots of existing atomic producer/worker counters. SDL reports
/// its own lightweight callback counters; no file I/O is performed by the
/// device callback or the PCM producer. Disabled unless explicitly enabled.
/// </summary>
public readonly record struct AudioDeviceCallbackObservation(
    long Calls, long RequestedFrames, long PeakRequestedFrames,
    long PeakElapsedTicks, long CallsOverNominalRequestDuration);

public sealed class AudioDeviceQualificationCsvRecorder : IDisposable
{
    public const string EnvironmentVariable = "HERESY_C5_AUDIO_CSV";
    public const int DefaultSampleIntervalMilliseconds = 250;

    private const string Header =
        "utc,session_id,record,elapsed_ms,sample_rate,channels," +
        "ring_capacity_frames,producer_quantum_frames,buffered_frames," +
        "underruns,producer_blocks,producer_frames,producer_over_budget," +
        "producer_peak_ms,producer_elapsed_ms," +
        "worker_multi_completed,worker_multi_cancelled," +
        "worker_multi_prepared_children,worker_queue_peak," +
        "worker_multi_allocated_bytes,worker_multi_abandoned_bytes," +
        "worker_multi_elapsed_ms,callback_calls,callback_requested_frames," +
        "callback_peak_requested_frames,callback_peak_ms," +
        "callback_over_nominal_request_duration,fault_type";

    private readonly object _gate = new();
    private readonly BufferedAudioOutputSource _source;
    private readonly Func<AudioDeviceCallbackObservation> _callbacks;
    private readonly Func<Exception?> _fault;
    private readonly StreamWriter _writer;
    private readonly Timer _timer;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _disposed;
    private bool _writeErrorReported;

    public AudioDeviceQualificationCsvRecorder(
        string path, BufferedAudioOutputSource source,
        Func<AudioDeviceCallbackObservation> callbackSnapshot,
        Func<Exception?> fault,
        int sampleIntervalMilliseconds = DefaultSampleIntervalMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _callbacks = callbackSnapshot
            ?? throw new ArgumentNullException(nameof(callbackSnapshot));
        _fault = fault ?? throw new ArgumentNullException(nameof(fault));
        if (sampleIntervalMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleIntervalMilliseconds));

        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        bool writeHeader = !File.Exists(absolute)
            || new FileInfo(absolute).Length == 0;
        _writer = new StreamWriter(new FileStream(absolute, FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        try
        {
            if (writeHeader)
                _writer.WriteLine(Header);
            WriteSample("start");
            _timer = new Timer(_ => SampleNow(), null,
                sampleIntervalMilliseconds, sampleIntervalMilliseconds);
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    /// <summary>Explicit off-callback sample for deterministic tests.</summary>
    public void SampleNow()
    {
        lock (_gate)
        {
            if (!_disposed)
                TryWriteSample("sample");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_disposed)
                return;
            TryWriteSample("stop");
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void TryWriteSample(string kind)
    {
        try
        {
            WriteSample(kind);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A full/unwritable diagnostic destination must not bring down
            // the application's timer thread or interfere with audio.
            if (!_writeErrorReported)
            {
                _writeErrorReported = true;
                Console.Error.WriteLine(
                    $"C5(e) audio CSV recording failed: {error.Message}");
            }
        }
    }

    private void WriteSample(string kind)
    {
        AudioDeviceCallbackObservation callback = _callbacks();
        Exception? fault = _fault() ?? _source.RenderingFault;
        CultureInfo inv = CultureInfo.InvariantCulture;
        double Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds;
        double CallbackMilliseconds(long ticks) =>
            1000.0 * ticks / Stopwatch.Frequency;
        string row = string.Join(",", new[]
        {
            DateTimeOffset.UtcNow.ToString("O", inv),
            _sessionId,
            kind,
            Milliseconds(Stopwatch.GetElapsedTime(_started)).ToString("F3", inv),
            _source.Format.SampleRate.ToString(inv),
            _source.Format.ChannelCount.ToString(inv),
            _source.CapacityFrames.ToString(inv),
            _source.RenderBlockFrames.ToString(inv),
            _source.BufferedFrames.ToString(inv),
            _source.UnderrunCount.ToString(inv),
            _source.ProducerRenderBlockCount.ToString(inv),
            _source.ProducerRenderedFrames.ToString(inv),
            _source.ProducerBlocksOverBudget.ToString(inv),
            Milliseconds(_source.PeakProducerRenderBlockTime).ToString("F3", inv),
            Milliseconds(_source.ProducerRenderElapsedTime).ToString("F3", inv),
            _source.CompletedMultiExpansions.ToString(inv),
            _source.CancelledMultiExpansions.ToString(inv),
            _source.PreparedMultiConstituents.ToString(inv),
            _source.PeakPreparationQueueDepth.ToString(inv),
            _source.MultiPreparationAllocatedBytes.ToString(inv),
            _source.AbandonedMultiPreparationAllocatedBytes.ToString(inv),
            Milliseconds(_source.MultiPreparationElapsedTime).ToString("F3", inv),
            callback.Calls.ToString(inv),
            callback.RequestedFrames.ToString(inv),
            callback.PeakRequestedFrames.ToString(inv),
            CallbackMilliseconds(callback.PeakElapsedTicks).ToString("F3", inv),
            callback.CallsOverNominalRequestDuration.ToString(inv),
            fault?.GetType().Name ?? "",
        });
        _writer.WriteLine(row);
    }
}
