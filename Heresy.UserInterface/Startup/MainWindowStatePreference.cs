using System;
using System.IO;

namespace Heresy.UserInterface.Startup;

/// <summary>
/// Versioned, best-effort preference for the desktop main window. Kept
/// outside song persistence: read/write failures must not prevent launch.
/// Writes are atomic so a crash cannot leave a partially written value.
/// </summary>
public sealed class MainWindowStatePreference
{
	private const string Maximized = "v1:maximized";
	private const string Normal = "v1:normal";
	private readonly string _path;

	public MainWindowStatePreference(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
	}

	public static MainWindowStatePreference ForCurrentUser()
	{
		string root = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(root))
			root = Path.Combine(Path.GetTempPath(), "Heresy");
		return new MainWindowStatePreference(Path.Combine(
			root, "Heresy", "main-window-state.v1"));
	}

	public bool? Read()
	{
		try
		{
			return File.ReadAllText(_path).Trim() switch
			{
				Maximized => true,
				Normal => false,
				_ => null,
			};
		}
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
	}

	public bool TrySave(bool maximized)
	{
		string? folder = Path.GetDirectoryName(_path);
		if (string.IsNullOrEmpty(folder))
			return false;
		string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			Directory.CreateDirectory(folder);
			File.WriteAllText(temp, maximized ? Maximized : Normal);
			File.Move(temp, _path, overwrite: true);
			return true;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
		finally
		{
			try { if (File.Exists(temp)) File.Delete(temp); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}
}

/// <summary>
/// Tracks the binary user preference independently from transient window
/// states. Minimize and fullscreen cannot erase the last known maximized
/// state. Returns a value only for actual preference changes.
/// </summary>
public sealed class MainWindowMaximizedTracker
{
	private bool _last;

	public MainWindowMaximizedTracker(bool initiallyMaximized)
		=> _last = initiallyMaximized;

	public bool IsMaximized => _last;

	public bool? Record(bool? maximizedState)
	{
		if (maximizedState is not bool requested || requested == _last)
			return null;
		_last = requested;
		return requested;
	}
}
