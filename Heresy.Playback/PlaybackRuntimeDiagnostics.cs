using System;
using System.Collections.Generic;

using Heresy.Core.Diagnostics;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

/// <summary>
/// Reports diagnostic records produced while preparing a playback source.
/// A report is associated with exactly one request, and is taken once by
/// the transport after preparation, off the realtime audio callback.
/// </summary>
public interface IPlaybackRuntimeDiagnosticReportProvider
{
	bool TryTakeRuntimeDiagnostics(
		PlaybackRequest request,
		out SequencingDiagnostic[] diagnostics);
}

/// <summary>
/// Optional playback transport event for recoverable sequencing warnings.
/// The event is published on the playback-command continuation, not the
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
