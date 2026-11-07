using System;
using System.Threading;

namespace Heresy.Render.Realtime;

/// <summary>
/// Optional lock-free mirror of float PCM successfully queued to the SDL
/// output stream. A session's callback is the only writer; a UI timer reads
/// the newest bounded window. This never affects the transport ring.
/// </summary>
public sealed class AudioOutputWaveformCapture
{
	public const int CapacityFrames = 16384;
	public const int MaximumCapturedChannels = 8;

	/// <summary>Opaque, per-SDL-session capture handle. A session replacement
	/// creates new storage, so old callback writes cannot corrupt a new scope.</summary>
	public sealed class Session
	{
		internal readonly float[] Samples;
		internal readonly long[] Stamps;
		internal long IssuedFrame;
		internal long PublishedFrame;
		internal long MinimumFrame = 1;

		internal Session(long id, AudioOutputFormat format)
		{
			Id = id;
			Format = format;
			CapturedChannels = Math.Min(
				format.ChannelCount, MaximumCapturedChannels);
			Samples = new float[CapacityFrames * CapturedChannels];
			Stamps = new long[CapacityFrames];
		}

		public long Id { get; }
		public AudioOutputFormat Format { get; }
		public int CapturedChannels { get; }
	}

	public readonly record struct Snapshot(
		long SessionId, int SampleRate, int ChannelCount, int VisibleFrames);

	private Session? _current;
	private long _nextSessionId;
	private int _enabled;

	public bool Enabled => Volatile.Read(ref _enabled) != 0;
	public long CurrentSessionId => Volatile.Read(ref _current)?.Id ?? 0;
	public int CurrentChannelCount =>
		Volatile.Read(ref _current)?.CapturedChannels ?? 0;

	public void SetEnabled(bool enabled)
	{
		if (enabled)
		{
			// Newly revealed scopes must not display stale frames retained
			// from an earlier visibility period.
			if (Interlocked.Exchange(ref _enabled, 1) == 0
				&& Volatile.Read(ref _current) is { } session)
			{
				Volatile.Write(ref session.MinimumFrame,
					Volatile.Read(ref session.PublishedFrame) + 1);
			}
		}
		else
		{
			Volatile.Write(ref _enabled, 0);
		}
	}

	/// <summary>Called when a new SDL output session is opened, not from
	/// its audio callback. Allocates fresh bounded history.</summary>
	public Session BeginSession(AudioOutputFormat format)
	{
		Session session = new(
			Interlocked.Increment(ref _nextSessionId), format);
		Volatile.Write(ref _current, session);
		return session;
	}

	/// <summary>Only the currently selected session can clear the UI
	/// history. Closing an old session must not hide its replacement.</summary>
	public void EndSession(Session session)
	{
		ArgumentNullException.ThrowIfNull(session);
		Interlocked.CompareExchange(ref _current, null, session);
	}

	/// <summary>
	/// Realtime callback path: no locks, allocations, blocking, or UI work.
	/// Caller invokes this only after SDL accepted the final interleaved
	/// float buffer; underrun-filled silence is included unchanged.
	/// </summary>
	public void Publish(Session session, ReadOnlySpan<float> interleaved)
	{
		if (Volatile.Read(ref _enabled) == 0
			|| !ReferenceEquals(session, Volatile.Read(ref _current)))
			return;

		int sourceChannels = session.Format.ChannelCount;
		int frames = interleaved.Length / sourceChannels;
		int channels = session.CapturedChannels;
		for (int frame = 0; frame < frames; frame++)
		{
			long sequence = ++session.IssuedFrame;
			int slot = (int)(sequence % CapacityFrames);
			long finishedStamp = sequence * 2;
			Volatile.Write(ref session.Stamps[slot], finishedStamp + 1);
			int start = slot * channels;
			int source = frame * sourceChannels;
			for (int channel = 0; channel < channels; channel++)
				session.Samples[start + channel] = interleaved[source + channel];
			Volatile.Write(ref session.Stamps[slot], finishedStamp);
			Volatile.Write(ref session.PublishedFrame, sequence);
		}
	}

	/// <summary>
	/// Fill two pixel-column arrays with the newest individual PCM frames.
	/// Missing/overwritten frames are NaN, not fabricated zero samples.
	/// Entries are right-aligned, one real frame per element. A second
	/// channel index of -1 disables its trace. Calls are UI-thread-only.
	/// </summary>
	public Snapshot ReadLatest(
		Span<float> first, Span<float> second,
		int firstChannel = 0, int secondChannel = -1)
	{
		if (first.Length != second.Length)
			throw new ArgumentException("Both scope buffers must have equal length.");
		first.Fill(float.NaN);
		second.Fill(float.NaN);

		Session? session = Volatile.Read(ref _current);
		if (session is null || Volatile.Read(ref _enabled) == 0)
			return default;

		int channels = session.CapturedChannels;
		firstChannel = Math.Clamp(firstChannel, 0, channels - 1);
		if (secondChannel < 0 || secondChannel >= channels)
			secondChannel = -1;
		long latest = Volatile.Read(ref session.PublishedFrame);
		long minimum = Volatile.Read(ref session.MinimumFrame);
		long available = Math.Max(0, latest - minimum + 1);
		int frames = (int)Math.Min(
			Math.Min(first.Length, CapacityFrames), available);
		long start = latest - frames + 1;
		int offset = first.Length - frames;

		for (int i = 0; i < frames; i++)
		{
			long sequence = start + i;
			int slot = (int)(sequence % CapacityFrames);
			long expectedStamp = sequence * 2;
			if (Volatile.Read(ref session.Stamps[slot]) != expectedStamp)
				continue;
			int sample = slot * channels;
			float a = session.Samples[sample + firstChannel];
			float b = secondChannel >= 0
				? session.Samples[sample + secondChannel] : float.NaN;
			if (Volatile.Read(ref session.Stamps[slot]) != expectedStamp)
				continue;
			first[offset + i] = a;
			second[offset + i] = b;
		}

		// Session replacement while reading is an entirely empty scope,
		// never a blend of unrelated PCM formats or playback sessions.
		if (!ReferenceEquals(session, Volatile.Read(ref _current)))
		{
			first.Fill(float.NaN);
			second.Fill(float.NaN);
			return default;
		}
		return new Snapshot(session.Id, session.Format.SampleRate,
			channels, frames);
	}
}
