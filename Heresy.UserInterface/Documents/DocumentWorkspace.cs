using System;
using System.IO;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Scripting.Analysis;

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

	public JsonAssetPathMode? JsonPathMode { get; private set; }

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
		JsonPathMode = null;
		_savedDocumentRevision = Document.DocumentRevision;
	}

	public void Open(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		string fullPath = Path.GetFullPath(path);
		SongDocument document = SongDocumentStorage.Load(fullPath);

		// Decode/load and resolve the persisted path mode *before*
		// changing the active workspace. A failed load leaves the old
		// document, dirty state, path and save preference intact.
		JsonAssetPathMode? mode = SongDocumentStorage.IsJsonPath(fullPath)
			? SongDocumentStorage.DetectJsonPathMode(fullPath)
			: null;
		Document = document;
		FilePath = fullPath;
		JsonPathMode = mode;
		_savedDocumentRevision = document.DocumentRevision;
	}

	public void Save()
	{
		if (FilePath is null)
			throw new InvalidOperationException(
				"The document does not yet have a file path. Use SaveAs first.");

		SongDocumentStorage.Save(
			FilePath,
			Document,
			JsonPathMode ?? JsonAssetPathMode.Relative,
			RoslynScriptObjectReferenceAnalyzer.Instance);
		_savedDocumentRevision = Document.DocumentRevision;
	}

	public void SaveAs(
		string path,
		JsonAssetPathMode jsonPathMode = JsonAssetPathMode.Relative)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		string fullPath = Path.GetFullPath(path);
		SongDocumentStorage.Save(
			fullPath,
			Document,
			jsonPathMode,
			RoslynScriptObjectReferenceAnalyzer.Instance);

		FilePath = fullPath;
		JsonPathMode = SongDocumentStorage.IsJsonPath(fullPath)
			? jsonPathMode
			: null;
		_savedDocumentRevision = Document.DocumentRevision;
	}
}
