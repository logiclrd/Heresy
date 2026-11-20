using System;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A semantic drop position, independent of Avalonia pixel coordinates.
/// The UI chooses a zone within the hovered row; validation and mutation
/// remain in SongTreeEditor so every document pane obeys the same rules.
/// </summary>
public enum SongTreeDropPlacement
{
    Before,
    Into,
    After,
}

public readonly record struct SongTreeDropPlan(
    SongTreeDropPlacement Placement,
    SongTreeNode Target)
{
    /// <summary>
    /// A folder row has insertion zones at the top/bottom and a central
    /// "into folder" zone, including for an empty folder. Ordinary object
    /// rows have before/after halves. Positions are normalized to [0,1].
    /// </summary>
    public static SongTreeDropPlan ForRow(
        SongTreeNode target,
        double relativeY)
    {
        ArgumentNullException.ThrowIfNull(target);
        double position = double.IsFinite(relativeY)
            ? Math.Clamp(relativeY, 0.0, 1.0)
            : 0.5;

        if (target is SongTreeFolder)
        {
            if (position < 0.25)
                return new(SongTreeDropPlacement.Before, target);
            if (position >= 0.75)
                return new(SongTreeDropPlacement.After, target);
            return new(SongTreeDropPlacement.Into, target);
        }

        return position < 0.5
            ? new(SongTreeDropPlacement.Before, target)
            : new(SongTreeDropPlacement.After, target);
    }

    /// <summary>
    /// The invisible fixed section root is addressed through an explicit
    /// UI drop surface (section heading or empty tree background).
    /// </summary>
    public static SongTreeDropPlan ForSectionRoot(SongTreeFolder sectionRoot)
    {
        ArgumentNullException.ThrowIfNull(sectionRoot);
        return new(SongTreeDropPlacement.Into, sectionRoot);
    }

    public bool CanApply(SongDocument document, SongTreeNode moving)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(moving);
        return Placement switch
        {
            SongTreeDropPlacement.Into when Target is SongTreeFolder folder =>
                SongTreeEditor.CanMoveInto(document, moving, folder),
            SongTreeDropPlacement.Before =>
                SongTreeEditor.CanMoveBefore(document, moving, Target),
            SongTreeDropPlacement.After =>
                SongTreeEditor.CanMoveAfter(document, moving, Target),
            _ => false,
        };
    }

    public void Apply(SongDocument document, SongTreeNode moving)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(moving);

        switch (Placement)
        {
            case SongTreeDropPlacement.Into when Target is SongTreeFolder folder:
                SongTreeEditor.MoveInto(document, moving, folder);
                break;
            case SongTreeDropPlacement.Before:
                SongTreeEditor.MoveBefore(document, moving, Target);
                break;
            case SongTreeDropPlacement.After:
                SongTreeEditor.MoveAfter(document, moving, Target);
                break;
            default:
                throw new InvalidOperationException("Invalid song-tree drop destination.");
        }
    }
}
