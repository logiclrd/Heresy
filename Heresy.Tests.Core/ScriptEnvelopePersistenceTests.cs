using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ScriptEnvelopePersistenceTests
{
	[Test]
	public void ScriptEnvelopeJsonAndSnapshotPreserveExpressionAndFiniteRelease()
	{
		SongDocument song = new();
		ObjectId id = song.AllocateObjectId();
		song.Add(new ScriptEnvelopeDefinition(id, "Expression")
		{
			Source = "releaseSeconds < 0 ? elapsedSeconds * -2.0 : 1.0 - releaseSeconds",
			ReleaseDuration = TimeSpan.FromMilliseconds(350),
		});
		const string path = "/tmp/expressions.hm.json";
		string json = SongDocumentJson.Serialize(song, path);
		Assert.That(json, Does.Contain("\"scriptEnvelope\""));
		SongDocument parsed = SongDocumentJson.Deserialize(json, path);
		ScriptEnvelopeDefinition loaded =
			(ScriptEnvelopeDefinition)parsed.Objects[id];
		Assert.Multiple(() =>
		{
			Assert.That(loaded.Source, Is.EqualTo(((ScriptEnvelopeDefinition)song.Objects[id]).Source));
			Assert.That(loaded.ReleaseDuration,
				Is.EqualTo(TimeSpan.FromMilliseconds(350)));
		});
		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(song);
		ScriptEnvelopeDefinition frozen =
			(ScriptEnvelopeDefinition)snapshot.Document.Objects[id];
		((ScriptEnvelopeDefinition)song.Objects[id]).Source = "9999.0";
		Assert.That(frozen.Source, Is.EqualTo(loaded.Source));
	}

	[Test]
	public void UnboundedReleaseRoundTripsWithoutInventingAnEnd()
	{
		SongDocument song = new();
		ObjectId id = song.AllocateObjectId();
		song.Add(new ScriptEnvelopeDefinition(id, "Indefinite")
			{ Source = "0.5", ReleaseDuration = null });
		const string path = "/tmp/indefinite.hm.json";
		SongDocument loaded = SongDocumentJson.Deserialize(
			SongDocumentJson.Serialize(song, path), path);
		Assert.That(((ScriptEnvelopeDefinition)loaded.Objects[id]).ReleaseDuration,
			Is.Null);
	}
}
