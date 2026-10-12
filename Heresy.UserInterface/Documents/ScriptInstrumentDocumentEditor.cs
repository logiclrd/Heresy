using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Persistence;
using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Validated, atomic authoring of compiled tone selectors and their finite,
/// explicitly referenced ToneSpecifications. No uncommitted invalid script
/// mutates the document or its audio revision.
/// </summary>
public static class ScriptInstrumentDocumentEditor
{
    public static ScriptInstrumentDefinition Create(
        DocumentWorkspace workspace, string name, string source = "-1")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateSource(source);
        ValidatePitchExpression("1.0");
        ScriptInstrumentDefinition definition =
            new(workspace.Document.AllocateObjectId(), name.Trim())
            { Source = source };
        workspace.Document.Add(definition, affectsAudio: true);
        return definition;
    }

    public static void SetSource(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        string source)
        => SetExpressions(workspace, definition, source,
            definition.PitchExpression);

    public static void SetExpressions(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        string source, string pitchExpression)
    {
        ValidateOwner(workspace, definition);
        ValidateSource(source);
        ValidatePitchExpression(pitchExpression);
        if (definition.Source == source
            && definition.PitchExpression == pitchExpression)
            return;
        definition.Source = source;
        definition.PitchExpression = pitchExpression;
        workspace.Document.MarkChanged(affectsAudio: true);
    }

    public static int AddTone(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        ObjectId soundId, double pitchMultiplier = 1.0,
        ObjectId? volumeEnvelopeId = null,
        ObjectId? pitchEnvelopeId = null,
        ObjectId? panningEnvelopeId = null,
        ObjectId? filterEnvelopeId = null,
        FilterInstance? filterConfiguration = null)
    {
        ValidateOwner(workspace, definition);
        ToneSpecification tone = InstrumentDocumentEditor.CreateToneSpecification(
            workspace.Document, soundId, pitchMultiplier, volumeEnvelopeId,
            pitchEnvelopeId, panningEnvelopeId, filterEnvelopeId, filterConfiguration);
        definition.ToneSpecifications.Add(tone);
        workspace.Document.MarkChanged(affectsAudio: true);
        return definition.ToneSpecifications.Count - 1;
    }

    public static void UpdateTone(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        int index, ObjectId soundId, double pitchMultiplier = 1.0,
        ObjectId? volumeEnvelopeId = null,
        ObjectId? pitchEnvelopeId = null,
        ObjectId? panningEnvelopeId = null,
        ObjectId? filterEnvelopeId = null,
        FilterInstance? filterConfiguration = null)
    {
        ValidateOwner(workspace, definition);
        ValidateIndex(definition, index);
        ToneSpecification replacement =
            InstrumentDocumentEditor.CreateToneSpecification(
                workspace.Document, soundId, pitchMultiplier,
                volumeEnvelopeId, pitchEnvelopeId,
                panningEnvelopeId, filterEnvelopeId, filterConfiguration);
        ToneSpecification old = definition.ToneSpecifications[index];
        if (old.SourceId == replacement.SourceId
            && old.PitchMultiplier == replacement.PitchMultiplier
            && old.VolumeEnvelopeId == replacement.VolumeEnvelopeId
            && old.PitchEnvelopeId == replacement.PitchEnvelopeId
            && old.PanningEnvelopeId == replacement.PanningEnvelopeId
            && old.FilterEnvelopeId == replacement.FilterEnvelopeId
            && old.Nna == replacement.Nna
            && (old.FilterConfiguration is null
                ? replacement.FilterConfiguration is null
                : replacement.FilterConfiguration is not null
                    && SongDocumentJson.SerializeFilterInstance(old.FilterConfiguration)
                        == SongDocumentJson.SerializeFilterInstance(replacement.FilterConfiguration)))
            return;
        definition.ToneSpecifications[index] = replacement;
        workspace.Document.MarkChanged(affectsAudio: true);
    }

    /// <summary>Publish only this tone's Filter override without accidentally
    /// committing unrelated text fields in the Script Instrument editor.</summary>
    public static void SetToneFilterConfiguration(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        int index, FilterInstance? configuration)
    {
        ValidateOwner(workspace, definition);
        ValidateIndex(definition, index);
        ToneSpecification old = definition.ToneSpecifications[index];
        FilterInstance? copied = InstrumentDocumentEditor.ValidateFilterConfiguration(
            workspace.Document, configuration);
        if (old.FilterConfiguration is null ? copied is null
            : copied is not null
                && SongDocumentJson.SerializeFilterInstance(old.FilterConfiguration)
                    == SongDocumentJson.SerializeFilterInstance(copied))
            return;
        definition.ToneSpecifications[index] = new ToneSpecification
        {
            SourceId = old.SourceId,
            PitchMultiplier = old.PitchMultiplier,
            VolumeEnvelopeId = old.VolumeEnvelopeId,
            PitchEnvelopeId = old.PitchEnvelopeId,
            PanningEnvelopeId = old.PanningEnvelopeId,
            FilterEnvelopeId = old.FilterEnvelopeId,
            FilterConfiguration = copied,
            Nna = old.Nna,
        };
        workspace.Document.MarkChanged(affectsAudio: true);
    }

    public static void RemoveTone(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition, int index)
    {
        ValidateOwner(workspace, definition);
        ValidateIndex(definition, index);
        definition.ToneSpecifications.RemoveAt(index);
        workspace.Document.MarkChanged(affectsAudio: true);
    }

    private static void ValidateOwner(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(definition);
        if (!workspace.Document.TryGet(definition.Id, out SongObject? stored)
            || !ReferenceEquals(stored, definition))
            throw new InvalidOperationException(
                "The script Instrument is not part of the active song document.");
    }

    private static void ValidateIndex(ScriptInstrumentDefinition definition, int index)
    {
        if ((uint)index >= (uint)definition.ToneSpecifications.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static void ValidatePitchExpression(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
            ScriptCompiler.AnalyzeInstrumentPitchSource(source);
        if (diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error))
            throw new ArgumentException(
                string.Join("; ", diagnostics.Select(d => d.Message)),
                nameof(source));
    }

    private static void ValidateSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
            ScriptCompiler.AnalyzeInstrumentSource(source);
        if (diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error))
            throw new ArgumentException(
                string.Join("; ", diagnostics.Select(d => d.Message)),
                nameof(source));
    }
}
