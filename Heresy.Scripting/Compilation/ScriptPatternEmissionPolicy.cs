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
            "Note" or "NoteVirtual" or "NoteFilter"
            or "ChannelFilter"
            or "Off" or "Cut"
            or "OffVirtualInScope" or "CutVirtualInScope"
            or "OffAllVirtual" or "CutAllVirtual"
            or "Volume" or "SourceFrame" or "Displacement" or "Past"
            or "Tempo" or "Speed" or "Seek" or "Pan" or "Surround"
            or "Filter" or "FilterCutoff" or "FilterResonance";

    internal static bool IsVirtualNoteEmitterName(string name)
        => name is "Note" or "Volume" or "Off" or "Cut" or "NoteFilter"
            or "ChannelFilter" or "SourceFrame" or "Displacement" or "Past"
            or "Seek" or "Pan" or "Surround" or "Filter" or "FilterCutoff" or "FilterResonance";

    internal static bool IsNoteGroupEmitterName(string name)
        => name != "Note" && IsVirtualNoteEmitterName(name);

    internal static bool IsNoteGroupTransfer(IMethodSymbol method,
        Microsoft.CodeAnalysis.Compilation compilation)
        => SymbolEqualityComparer.Default.Equals(method.ContainingType,
                compilation.GetTypeByMetadataName(typeof(NoteGroup).FullName!))
            && method.Name == "op_AdditionAssignment"
            && method.MethodKind == MethodKind.UserDefinedOperator
            && !method.IsStatic && method.ReturnsVoid
            && method.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type,
                compilation.GetTypeByMetadataName(typeof(VirtualNote).FullName!));

    internal static bool IsEmitterMethod(IMethodSymbol method,
        Microsoft.CodeAnalysis.Compilation compilation)
    {
        INamedTypeSymbol? patternBase = compilation.GetTypeByMetadataName(
            typeof(PatternScriptProgram).FullName!);
        return patternBase is not null
            && SymbolEqualityComparer.Default.Equals(
                method.ContainingType, patternBase)
            && IsEmitterName(method.Name)
            || SymbolEqualityComparer.Default.Equals(method.ContainingType,
                compilation.GetTypeByMetadataName(typeof(VirtualNote).FullName!))
                && IsVirtualNoteEmitterName(method.Name)
            || SymbolEqualityComparer.Default.Equals(method.ContainingType,
                compilation.GetTypeByMetadataName(typeof(NoteGroup).FullName!))
                && IsNoteGroupEmitterName(method.Name);
    }
}
