using System;
using System.IO;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Owns the single active authoring document and the persistence baseline used
/// by the UI's saved/modified state. The semantic document remains in Core.
/// </summary>
public sealed class DocumentWorkspace
{
	private uint _savedDocumentRevision;

	public DocumentWorkspace()
	{
		Document = new SongDocument();
		_savedDocumentRevision = Document.DocumentRevision;
	}

	public SongDocument Document { get; private set; }

	public string? FilePath { get; private set; }

	public string DisplayName
		=> FilePath is null
			? "Untitled"
			: Path.GetFileName(FilePath);

	public bool IsModified
		=> Document.DocumentRevision != _savedDocumentRevision;

	public void New()
	{
		Document = new SongDocument();
		FilePath = null;
		_savedDocumentRevision = Document.DocumentRevision;
	}

	public void Open(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		string fullPath = Path.GetFullPath(path);
		SongDocument document = SongDocumentJson.Load(fullPath);

		Document = document;
		FilePath = fullPath;
		_savedDocumentRevision = document.DocumentRevision;
	}

	public void Save()
	{
		if (FilePath is null)
			throw new InvalidOperationException(
				"The document does not yet have a file path. Use SaveAs first.");

		SongDocumentJson.Save(FilePath, Document);
		_savedDocumentRevision = Document.DocumentRevision;
	}

	public void SaveAs(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		string fullPath = Path.GetFullPath(path);
		SongDocumentJson.Save(fullPath, Document);

		FilePath = fullPath;
		_savedDocumentRevision = Document.DocumentRevision;
	}
}
