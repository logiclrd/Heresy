using System;
using System.Collections.Generic;

using Heresy.Core.Diagnostics;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

/// <summary>
/// Reports diagnostic records produced as an incremental playback source
/// executes. Reports are drained from the PCM worker and can be polled
/// repeatedly by the transport without touching scripting state.
/// </summary>
public interface IPlaybackRuntimeDiagnosticReportProvider
{
	bool TryTakeRuntimeDiagnostics(
		PlaybackRequest request,
		out SequencingDiagnostic[] diagnostics);
}

/// <summary>
/// Optional playback transport event for recoverable sequencing warnings.
/// The event is published on the transport command/timer thread, not the
/// audio callback. UI consumers must dispatch to the UI thread.
/// </summary>
public interface IPlaybackRuntimeDiagnosticsTransport
{
	event EventHandler<PlaybackRuntimeDiagnosticsEventArgs>? RuntimeDiagnostics;
}

public sealed class PlaybackRuntimeDiagnosticsEventArgs : EventArgs
{
	public PlaybackRuntimeDiagnosticsEventArgs(
		IReadOnlyList<SequencingDiagnostic> diagnostics)
		=> Diagnostics = diagnostics
			?? throw new ArgumentNullException(nameof(diagnostics));

	public IReadOnlyList<SequencingDiagnostic> Diagnostics { get; }
}
