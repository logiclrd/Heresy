using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.CodeAnalysis;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Symbol-based representation of the owner-approved Pattern/Sequence runtime
/// allow-list. This is policy infrastructure, NOT YET a compiler validator:
/// callers must also inspect all bound operations, implicit calls, and types.
/// </summary>
internal sealed class ScriptRuntimeApiPolicy
{
    private const string CoreLibraryName = "System.Private.CoreLib";
    private const string ResourceName = "Heresy.Scripting.script-runtime-allow-list.txt";
    private const string GlobalizationPrefix = "System.Globalization.";

    private static readonly Lazy<ScriptRuntimeApiPolicy> Loaded =
        new(LoadEmbedded);

    private readonly HashSet<string> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _namespacePrefixes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedMembers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedConstructors = new(StringComparer.Ordinal);

    private bool _allowCoreLibraryEnums;
    private bool _allowCoreLibraryExceptions;
    private bool _allowCoreLibraryInterfaces;
    private bool _allowCalendarSubclasses;
    private bool _blockGlobalizationPropertyWrites;
    private bool _blockPathReaderWriterConstructors;
    private bool _blockUserFinalizers;

    private ScriptRuntimeApiPolicy() { }

    internal static ScriptRuntimeApiPolicy Default => Loaded.Value;

    /// <summary>The exact policy lines that have been interpreted, including
    /// deliberate duplicate restrictions; an unknown line is a hard error.</summary>
    internal int EntryCount { get; private set; }

    private static ScriptRuntimeApiPolicy LoadEmbedded()
    {
        using Stream stream = typeof(ScriptRuntimeApiPolicy).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "The Script Pattern/Sequence runtime API allow-list is missing.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static ScriptRuntimeApiPolicy Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ScriptRuntimeApiPolicy policy = new();
        string section = "";
        foreach (string sourceLine in text.Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0 || line is "Allow list" or "=========="
                || line.StartsWith("Important: ", StringComparison.Ordinal))
                continue;

            if (line is "All enumerated types defined within System.PrivateCoreLib")
            {
                policy._allowCoreLibraryEnums = true;
                policy.EntryCount++;
                continue;
            }

            if (line.EndsWith(':'))
            {
                if (line is not ("Basic types:" or "Exceptions:" or "Core types:"
                    or "Generic building blocks:" or "Interfaces:"
                    or "Selectively block:"))
                    throw new FormatException("Unknown script API policy section: " + line);
                section = line;
                continue;
            }

            if (section == "Selectively block:")
            {
                policy.AddExclusion(line);
                policy.EntryCount++;
                continue;
            }

            if (line is "System.Exception and any subclass defined within System.Private.CoreLib"
                && section == "Exceptions:")
                policy._allowCoreLibraryExceptions = true;
            else if (line is "Any interface defined in System.Private.CoreLib"
                && section == "Interfaces:")
                policy._allowCoreLibraryInterfaces = true;
            else if (line is "System.Globalization.Calendar and all subclasses"
                && section == "Core types:")
            {
                policy._types.Add("System.Globalization.Calendar");
                policy._allowCalendarSubclasses = true;
            }
            else if (section is "Basic types:" or "Core types:"
                or "Generic building blocks:")
            {
                string name = line.EndsWith(" (*see below)", StringComparison.Ordinal)
                    ? line[..^" (*see below)".Length] : line;
                if (name.EndsWith(".*", StringComparison.Ordinal))
                    policy._namespacePrefixes.Add(name[..^1]);
                else if (name.StartsWith("System.", StringComparison.Ordinal)
                    && !name.Contains(' ') && !name.Contains(':'))
                    policy._types.Add(name);
                else
                    throw new FormatException("Unrecognized script API type: " + line);
            }
            else
                throw new FormatException("Unrecognized script API policy entry: " + line);

            policy.EntryCount++;
        }

        if (!policy._allowCoreLibraryEnums || !policy._allowCoreLibraryExceptions
            || !policy._allowCoreLibraryInterfaces || !policy._allowCalendarSubclasses
            || !policy._blockGlobalizationPropertyWrites
            || !policy._blockPathReaderWriterConstructors || !policy._blockUserFinalizers)
            throw new FormatException("The script API policy is missing a required category.");

        return policy;
    }

    private void AddExclusion(string line)
    {
        if (line == "User-defined finalizers")
        {
            _blockUserFinalizers = true;
            return;
        }

        if (line == "System.Globalization.*: property assignment")
        {
            _blockGlobalizationPropertyWrites = true;
            return;
        }

        if (line == "System.IO.StreamReader, System.IO.StreamWriter: constructors that take file paths")
        {
            _blockPathReaderWriterConstructors = true;
            return;
        }

        if (line == "System.Globalization.CultureInfo: instantiation, ClearCachedData")
        {
            _blockedConstructors.Add("System.Globalization.CultureInfo");
            _blockedMembers.Add("System.Globalization.CultureInfo::ClearCachedData");
            return;
        }

        int separator = line.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0)
            throw new FormatException("Unrecognized script API member exclusion: " + line);

        string type = line[..separator];
        string member = line[(separator + 2)..].TrimEnd('(', ')');
        if (type == "Object")
            type = "System.Object";

        if (!type.StartsWith("System.", StringComparison.Ordinal)
            || member.Length == 0 || member.Any(char.IsWhiteSpace))
            throw new FormatException("Invalid script API member exclusion: " + line);

        _blockedMembers.Add(type + "::" + member);
    }

    /// <summary>
    /// Check a framework type against its actual defining assembly, not an
    /// author-controlled namespace or an untrusted assembly with the same name.
    /// This method deliberately does NOT grant permission to user-declared types.
    /// </summary>
    internal bool IsAllowedRuntimeType(ITypeSymbol type, Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(compilation);

        if (type is IArrayTypeSymbol array)
            return IsAllowedRuntimeType(array.ElementType, compilation);
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol
            or IDynamicTypeSymbol or IErrorTypeSymbol)
            return false;
        if (type is not INamedTypeSymbol named)
            return false;

        // Compare the actual core-library symbol identity, not just its name.
        IAssemblySymbol core = compilation.GetSpecialType(
            SpecialType.System_Object).ContainingAssembly;
        if (core.Identity.Name != CoreLibraryName
            || !SymbolEqualityComparer.Default.Equals(
                named.ContainingAssembly, core))
            return false;

        // An open generic definition contains its own type parameters, while
        // a constructed type must validate every *actual* type argument.
        bool isDefinition = SymbolEqualityComparer.Default.Equals(
            named, named.OriginalDefinition);
        foreach (ITypeSymbol argument in named.TypeArguments)
        {
            if (isDefinition && argument is ITypeParameterSymbol)
                continue;
            if (!IsAllowedRuntimeType(argument, compilation))
                return false;
        }
        if (named.ContainingType is { } containing
            && !IsAllowedRuntimeType(containing, compilation))
            return false;

        INamedTypeSymbol definition = named.OriginalDefinition;
        string name = MetadataName(definition);
        if (_types.Contains(name)
            || _namespacePrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.Ordinal))
            || (_allowCoreLibraryEnums && definition.TypeKind == TypeKind.Enum)
            || (_allowCoreLibraryInterfaces && definition.TypeKind == TypeKind.Interface)
            || (_allowCoreLibraryExceptions && HasBase(definition, "System.Exception"))
            || (_allowCalendarSubclasses
                && HasBase(definition, "System.Globalization.Calendar")))
            return true;

        return false;
    }

    /// <summary>
    /// A blocked member remains blocked for direct calls, method group
    /// conversions, and delegate targets. The future operation verifier must
    /// call this for implicit property setters and constructor invocations too.
    /// A false result does not imply its containing type is allowed.
    /// </summary>
    internal bool IsBlockedMember(ISymbol symbol, ScriptMemberUse use,
        Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(compilation);

        if (symbol is IMethodSymbol accessor && accessor.AssociatedSymbol is
            IPropertySymbol property)
            symbol = property;

        // Finalizers declared in authored types may run on the GC thread.
        if (_blockUserFinalizers && symbol is IMethodSymbol finalizer
            && finalizer.MethodKind == MethodKind.Destructor
            && !IsCoreLibrarySymbol(finalizer, compilation))
            return true;

        if (!IsCoreLibrarySymbol(symbol, compilation))
            return false; // The general type/operation verifier must handle this.

        if (symbol is IMethodSymbol constructor
            && constructor.MethodKind == MethodKind.Constructor)
        {
            string owner = MetadataName(constructor.ContainingType);
            if (_blockedConstructors.Contains(owner))
                return true;
            if (_blockPathReaderWriterConstructors
                && (owner is "System.IO.StreamReader" or "System.IO.StreamWriter")
                && constructor.Parameters.Length > 0
                && constructor.Parameters[0].Type.SpecialType == SpecialType.System_String)
                return true;
        }

        if (_blockGlobalizationPropertyWrites && use == ScriptMemberUse.Write
            && symbol is IPropertySymbol writableProperty
            && MetadataName(writableProperty.ContainingType)
                .StartsWith(GlobalizationPrefix, StringComparison.Ordinal))
            return true;

        if (symbol.ContainingType is not INamedTypeSymbol containingType)
            return false;

        string key = MetadataName(containingType.OriginalDefinition)
            + "::" + symbol.Name;
        return _blockedMembers.Contains(key);
    }

    private static bool IsCoreLibrarySymbol(ISymbol symbol, Compilation compilation)
        => SymbolEqualityComparer.Default.Equals(
            symbol.ContainingAssembly,
            compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly);

    private static bool HasBase(INamedTypeSymbol type, string baseName)
    {
        for (INamedTypeSymbol? current = type; current is not null;
            current = current.BaseType)
        {
            if (MetadataName(current.OriginalDefinition) == baseName)
                return true;
        }
        return false;
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        List<string> parts = [];
        for (INamedTypeSymbol? current = type; current is not null;
            current = current.ContainingType)
            parts.Insert(0, current.MetadataName);
        string nested = string.Join("+", parts);
        string ns = type.ContainingNamespace.ToDisplayString();
        return ns.Length == 0 ? nested : ns + "." + nested;
    }
}

/// <summary>For the member verifier: distinguish reading a property from
/// assigning to it, including compound assignments or increment operations.</summary>
internal enum ScriptMemberUse
{
    Read,
    Write,
    CallOrMethodGroup,
    Construct,
}
