using System;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterPaneTreeTests
{
	[Test]
	public void FiltersFolderCreationAndReorganizationKeepAudioRevisionAndObjectId()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		document.Add(new FilterDefinition(filterId, "EQ"));
		SongTreeFolder filters = document.GetSectionRoot(SongTreeSection.Filters);
		SongTreeObject filterNode = (SongTreeObject)filters.Children.Single();

		uint audioRevision = document.AudioRevision;
		SongTreeFolder favorites =
			SongTreeEditor.CreateFolder(document, filters, "Favorites");
		SongTreeFolder library =
			SongTreeEditor.CreateFolder(document, filters, "Library");
		SongTreeEditor.MoveInto(document, filterNode, favorites);
		SongTreeEditor.MoveInto(document, favorites, library);
		Assert.That(SongTreeEditor.GetSection(document, filterNode),
			Is.EqualTo(SongTreeSection.Filters));
		Assert.That(((SongTreeObject)((SongTreeFolder)library.Children.Single())
			.Children.Single()).ObjectId, Is.EqualTo(filterId));
		Assert.That(document.AudioRevision, Is.EqualTo(audioRevision));

		SongTreeEditor.RenameFolder(document, favorites, "Curated");
		Assert.That(favorites.Name, Is.EqualTo("Curated"));
		Assert.That(document.AudioRevision, Is.EqualTo(audioRevision));
	}

	[Test]
	public void FiltersFixedRootCannotBeRenamedDeletedOrMovedToOtherSection()
	{
		SongDocument document = new();
		SongTreeFolder filters = document.GetSectionRoot(SongTreeSection.Filters);
		SongTreeFolder patches = document.GetSectionRoot(SongTreeSection.Samples);
		SongTreeFolder custom = SongTreeEditor.CreateFolder(document, filters, "Custom");

		Assert.Throws<InvalidOperationException>(() =>
			SongTreeEditor.RenameFolder(document, filters, "Not Filters"));
		Assert.Throws<InvalidOperationException>(() =>
			SongTreeEditor.RemoveNode(document, filters));
		Assert.Throws<InvalidOperationException>(() =>
			SongTreeEditor.MoveInto(document, custom, patches));
		Assert.Throws<InvalidOperationException>(() =>
			SongTreeEditor.MoveInto(document, custom, document.Root));
		Assert.That(SongTreeEditor.CanMoveInto(document, custom, patches),
			Is.False);
		Assert.That(SongTreeEditor.GetSection(document, custom),
			Is.EqualTo(SongTreeSection.Filters));
	}

	[Test]
	public void FilterObjectsProjectIntoNewSectionWithoutPrematureEditorActivation()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new FilterDefinition(id, "Shared filter"));
		SongTreeObject node = (SongTreeObject)document
			.GetSectionRoot(SongTreeSection.Filters).Children.Single();
		SongTreeItemViewModel view =
			SongTreeItemViewModel.Create(document, node);

		Assert.That(view.ObjectId, Is.EqualTo(id));
		Assert.That(view.Kind, Is.EqualTo(SongObjectKind.Filter));
		Assert.That(SongTreeDefaultActivation.Resolve(SongTreeSection.Filters, view),
			Is.EqualTo(SongTreeActivationKind.None),
			"The standalone Filter editor is a separate milestone.");
	}
}
