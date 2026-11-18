using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Final result of the headless import transaction. Failed preparation and
/// failed publication leave the destination song unchanged. The undo receipt
/// is one indivisible document edit, not yet registered with a global UI stack.
/// </summary>
public sealed class FmImportCommitResult
{
    internal FmImportCommitResult(FmImportPreparationResult preparation,
        SongImportBatchEdit? edit, string? error)
    {
        Preparation = preparation;
        Edit = edit;
        Error = error;
    }

    public FmImportPreparationResult Preparation { get; }
    public SongImportBatchEdit? Edit { get; }
    public string? Error { get; }
    public bool IsCommitted => Edit is not null;
    public IReadOnlyList<ObjectId> SelectedRootIds =>
        IsCommitted ? Preparation.SelectedRootIds : [];
}

/// <summary>
/// Synchronous editor-thread import boundary: re-run Stage B and construct
/// detached copies, recheck source/destination fingerprints at the exact
/// publication point, and use Core's all-or-nothing batch insertion.
///
/// This is separate from the provisional-plan preview. The legacy UI import
/// command must not invoke it until dialog error/retry and undo integration
/// have their own acceptance coverage.
/// </summary>
public static class FmSynthImportTransactionCommitter
{
    public static FmImportCommitResult Commit(FmSynthImportPlan plan,
        IReadOnlyList<FmImportExactMatchSource> sources,
        SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, sources, destination);
        if (!prepared.IsPrepared)
        {
            string message = string.Join("\n",
                prepared.Preflight.Issues.Select(x => x.Message)
                    .Concat(prepared.Issues.Select(x => x.Message)));
            return new(prepared, null,
                string.IsNullOrWhiteSpace(message)
                    ? "The import could not be prepared." : message);
        }

        bool FreshAtPublication(SongDocument current)
        {
            if (!ReferenceEquals(current, destination)
                || current.NextUnallocatedObjectId != prepared.ExpectedNextId
                || !string.Equals(plan.DestinationDocumentFingerprint,
                    FmSynthImportAuthoringFingerprint.Capture(current),
                    StringComparison.Ordinal))
                return false;
            foreach (FmImportExactMatchSource source in sources)
            {
                string identity = source.Discovery.SourceDocumentIdentity;
                if (!plan.SourceDocumentFingerprints.TryGetValue(identity,
                        out string? fingerprint)
                    || !plan.SourceDocumentRevisions.TryGetValue(identity,
                        out uint revision)
                    || source.Source.Document.DocumentRevision != revision
                    || !string.Equals(fingerprint,
                        FmSynthImportAuthoringFingerprint.Capture(
                            source.Source.Document), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        try
        {
            SongImportBatchEdit edit = destination.CommitImportedObjects(
                prepared.PreparedObjects.Select(x => x.Object).ToArray(),
                prepared.ExpectedNextId, FreshAtPublication);
            return new(prepared, edit, null);
        }
        catch (Exception error) when (error is InvalidOperationException
            or ArgumentException or NotSupportedException or OverflowException
            or System.Text.Json.JsonException)
        {
            return new(prepared, null,
                $"The import was not applied: {error.Message}");
        }
    }
}
