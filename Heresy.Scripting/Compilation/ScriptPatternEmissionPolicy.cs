using Heresy.Scripting.Runtime;

using Microsoft.CodeAnalysis;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Pattern helpers that append a musical event to the invocation-local receiver.
/// In strict streaming, these are direct statement/yield boundaries, and cannot
/// be captured as delegates or called inside lambdas. Non-emitting helpers
/// (_O, Random, Checkpoint, etc.) do not share this restriction.
/// Keep the compiler's direct-statement check and yield/F6 rewrites in sync.
/// </summary>
internal static class ScriptPatternEmissionPolicy
{
    internal static bool IsEmitterName(string name)
        => name is
            "Note" or "NoteVirtual" or "NoteFilter" or "NoteFilterVirtual"
            or "ChannelFilter" or "ChannelFilterVirtual"
            or "Off" or "Cut" or "OffVirtual" or "CutVirtual"
            or "OffVirtualInScope" or "CutVirtualInScope"
            or "OffAllVirtual" or "CutAllVirtual"
            or "Tempo" or "Speed" or "Seek" or "Pan" or "Surround"
            or "Filter" or "FilterCutoff" or "FilterResonance"
            or "VolumeVirtual" or "SourceFrameVirtual"
            or "DisplacementVirtual" or "PastVirtual";

    internal static bool IsEmitterMethod(IMethodSymbol method,
        Compilation compilation)
    {
        INamedTypeSymbol? patternBase = compilation.GetTypeByMetadataName(
            typeof(PatternScriptProgram).FullName!);
        return patternBase is not null
            && SymbolEqualityComparer.Default.Equals(
                method.ContainingType, patternBase)
            && IsEmitterName(method.Name);
    }
}
