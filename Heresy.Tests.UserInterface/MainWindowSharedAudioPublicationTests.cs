using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

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
