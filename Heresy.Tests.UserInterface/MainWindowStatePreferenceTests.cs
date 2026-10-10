using System;
using System.IO;

using Heresy.UserInterface.Startup;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class MainWindowStatePreferenceTests
{
	[Test]
	public void FirstRunAndMalformedSavedStateAreIgnored()
	{
		string folder = Path.Combine(Path.GetTempPath(),
			"heresy-window-pref-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(folder, "main-window-state.v1");
		try
		{
			MainWindowStatePreference prefs = new(path);
			Assert.That(prefs.Read(), Is.Null);
			Directory.CreateDirectory(folder);
			File.WriteAllText(path, "not-a-valid-state");
			Assert.That(prefs.Read(), Is.Null);
		}
		finally
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, recursive: true);
		}
	}

	[Test]
	public void MaximizedAndRestoredStatesRoundTripOnEachTransition()
	{
		string folder = Path.Combine(Path.GetTempPath(),
			"heresy-window-pref-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(folder, "main-window-state.v1");
		try
		{
			MainWindowStatePreference prefs = new(path);
			Assert.That(prefs.TrySave(true), Is.True);
			Assert.That(new MainWindowStatePreference(path).Read(), Is.True);
			Assert.That(prefs.TrySave(false), Is.True);
			Assert.That(new MainWindowStatePreference(path).Read(), Is.False);
			Assert.That(Directory.GetFiles(folder, "*.tmp"), Is.Empty);
		}
		finally
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, recursive: true);
		}
	}

	[Test]
	public void UnavailablePreferenceDirectoryFailsWithoutThrowing()
	{
		string folder = Path.Combine(Path.GetTempPath(),
			"heresy-window-pref-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			// A regular file cannot be created as a directory.
			string blocked = Path.Combine(folder, "blocked");
			File.WriteAllText(blocked, "x");
			MainWindowStatePreference prefs = new(Path.Combine(
				blocked, "main-window-state.v1"));
			Assert.That(prefs.Read(), Is.Null);
			Assert.That(prefs.TrySave(true), Is.False);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Test]
	public void MaximizeAndRestoreSaveImmediatelyButMinimizeAndFullscreenDoNot()
	{
		MainWindowMaximizedTracker tracker = new(false);
		Assert.That(tracker.Record(true), Is.True);
		Assert.That(tracker.Record(null), Is.Null);
		Assert.That(tracker.IsMaximized, Is.True);
		Assert.That(tracker.Record(true), Is.Null);
		Assert.That(tracker.Record(false), Is.False);
		Assert.That(tracker.Record(null), Is.Null);
		Assert.That(tracker.IsMaximized, Is.False);
	}

	[Test]
	public void RestoredPreferenceDoesNotEmitDuplicateStartupSave()
	{
		MainWindowMaximizedTracker tracker = new(true);
		Assert.That(tracker.Record(true), Is.Null);
		Assert.That(tracker.Record(null), Is.Null);
		Assert.That(tracker.Record(false), Is.False);
	}
}
