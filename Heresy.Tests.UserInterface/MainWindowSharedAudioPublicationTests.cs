using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class MainWindowSharedAudioPublicationTests
{
	[AvaloniaTest]
	public void StandaloneSharedEditsReachTheTransportButDisplayChangesDoNot()
	{
		RecordingTransport transport = new();
		MainWindow window = new(transport);
		DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
			.GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(window)!;
		window.Show();
		try
		{
			FilterDefinition filter =
				FilterDocumentEditor.CreateFilter(workspace, "Live");
			NodeEnvelopeDefinition envelope =
				EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Curve");
			Assert.That(transport.SharedIds, Is.Empty,
				"creation of an unreferenced object should not publish sound");

			FilterStageDefinition original = filter.GetStage(1);
			FilterDocumentEditor.ApplyStage(workspace, filter, 1, original,
				"retuned", original.Kind, original.DefaultParameters);
			Assert.That(transport.SharedIds, Is.EqualTo(new[] { filter.Id }));

			EnvelopeDocumentEditor.UpdateNodeEnvelopeDisplayRange(
				workspace, envelope, envelope.Vmin - 0.5,
				envelope.Vmax + 0.5);
			Assert.That(transport.SharedIds, Has.Count.EqualTo(1));

			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				workspace, envelope,
				[new EnvelopeNode(TimeSpan.Zero, 0.75)],
				0, 0, EnvelopeSustainLoopMode.Forward);
			Assert.That(transport.SharedIds,
				Is.EqualTo(new[] { filter.Id, envelope.Id }));
		}
		finally
		{
			// Close without prompting for unsaved changes. The existing
			// document is no longer active, so its queued callbacks would
			// also be ignored by the window's identity guard.
			workspace.New();
			window.Close();
		}
	}

	[AvaloniaTest]
	public void SnapshotWarningDescribesSessionBaseRatherThanClaimingOldSound()
	{
		RecordingTransport transport = new();
		MainWindow window = new(transport);
		DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
			.GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(window)!;
		TextBlock indicator = (TextBlock)typeof(MainWindow)
			.GetField("_playbackSnapshotIndicator",
				BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(window)!;
		window.Show();
		try
		{
			// A live Synth edit can have reached its existing audition
			// voice without refreshing the base song snapshot. The indicator
			// cannot truthfully claim that its *audio* must be old.
			SongDocument document = workspace.Document;
			typeof(MainWindow).GetField("_playbackSnapshot",
				BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(window, new PlaybackSnapshotInfo(
					1, document, document.AudioRevision));
			document.MarkChanged(affectsAudio: true);
			Assert.That(indicator.IsVisible, Is.True);
			Assert.That(indicator.Text, Is.EqualTo("Playback snapshot is older"));
			Assert.That(ToolTip.GetTip(indicator)?.ToString(),
				Does.Contain("Compatible live Synth edits"),
				"the explanation must acknowledge continued live audio updates");

			// The old snapshot is not a different *document*. This remains
			// separate from the genuine 'another song' warning.
			typeof(MainWindow).GetField("_playbackSnapshot",
				BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(window, new PlaybackSnapshotInfo(
					2, new SongDocument(), document.AudioRevision));
			typeof(MainWindow).GetMethod("UpdatePlaybackSnapshotIndicator",
				BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(window, null);
			Assert.That(indicator.Text, Is.EqualTo("Playback uses another song"));
		}
		finally
		{
			workspace.New();
			window.Close();
		}
	}

	private sealed class RecordingTransport :
		ISongPlaybackTransport, ILiveSynthEditTransport
	{
		public List<ObjectId> SharedIds { get; } = [];
		public Task PublishSharedObjectEditAsync(
			SongDocument document, ObjectId objectId)
		{
			SharedIds.Add(objectId);
			return Task.CompletedTask;
		}
		public Task PublishSynthEditAsync(
			SongDocument document, ObjectId sourceId)
			=> Task.CompletedTask;
		public Task PlaySongAsync(SongDocument document)
			=> Task.CompletedTask;
		public Task PlaySequenceAsync(SongDocument document,
			ObjectId sequenceId,
			SequencePlaybackPosition? startPosition = null)
			=> Task.CompletedTask;
		public Task PlayPatternAsync(SongDocument document,
			ObjectId patternId, int startRow = 0, bool repeat = false)
			=> Task.CompletedTask;
		public Task PlayAdHocAsync(
			SongDocument document, NoteSchedule schedule)
			=> Task.CompletedTask;
		public Task SendLiveEventAsync(SongDocument document,
			ChannelTarget target, IReadOnlyList<NoteCommand> commands)
			=> Task.CompletedTask;
		public Task StopAsync() => Task.CompletedTask;
		public void Dispose() { }
	}
}
