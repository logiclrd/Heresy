using System;
using System.Security.Cryptography;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Content-based freshness check for source/destination documents before a
/// later atomic import. SongDocumentRevision alone cannot detect direct
/// mutable-object property edits, and structural rediscovery cannot detect
/// oscillator parameters or envelope expression changes.
///
/// The Core snapshot serializer is explicitly read-only: ordinary song save
/// JSON serialization may prune tombstones. Sample encoded bytes are checked
/// separately by Stage B; this fingerprint captures authored metadata only.
/// </summary>
internal static class FmSynthImportAuthoringFingerprint
{
    public static string Capture(SongDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string snapshot = SongDocumentJson.SerializeAuthoringSnapshot(document);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }
}
