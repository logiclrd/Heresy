using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Runtime;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Heresy.Scripting.Compilation;

public sealed record ScriptCompilationResult<T>(
	T? Program,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
	where T : class
{
	public bool Success
	{
		get
		{
			if (Program is null)
				return false;

			foreach (ScriptAnalysisDiagnostic diagnostic in Diagnostics)
			{
				if (diagnostic.Severity == ScriptDiagnosticSeverity.Error)
					return false;
			}

			return true;
		}
	}
}

/// <summary>
/// Compiles Heresy's restricted C# source into the existing Core sequencing
/// contracts. Compilation emits an in-memory assembly only after syntax and
/// semantic allow-list validation have succeeded.
/// </summary>
public static class ScriptCompiler
{
	private const string RestrictedFeatureCode = "HRS2001";
	private const string ScriptPath = "heresy-script";

	private static readonly CSharpParseOptions WrapperParseOptions =
		new(LanguageVersion.Preview);

	private static readonly CSharpParseOptions ScriptParseOptions =
		new(
			LanguageVersion.Preview,
			kind: SourceCodeKind.Script);

	private static readonly MetadataReference[] MetadataReferences =
		CreateMetadataReferences();

	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzePatternSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);

		const string className = "__HeresyPattern_Analysis";
		return PrepareCompilation(
			source,
			BuildPatternWrapper(
				className,
				InstrumentLoops(source)),
			wrapperKind: ScriptWrapperKind.Pattern)
			.Diagnostics;
	}

	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeSequenceSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);

		const string className = "__HeresySequence_Analysis";
		return PrepareCompilation(
			source,
			BuildSequenceWrapper(
				className,
				InstrumentLoops(source)),
			wrapperKind: ScriptWrapperKind.Sequence)
			.Diagnostics;
	}

	public static ScriptCompilationResult<IRawPatternNoteGenerator> CompilePattern(
		ScriptPatternDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);

		string className =
			"__HeresyPattern_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled =
			CompileType(
				definition.Source,
				className,
				BuildPatternWrapper(
					className,
					InstrumentLoops(definition.Source)),
				typeof(PatternScriptProgram));

		if (!compiled.Success || compiled.Program is null)
		{
			return new ScriptCompilationResult<IRawPatternNoteGenerator>(
				null,
				compiled.Diagnostics);
		}

		return new(
			new CompiledPatternGenerator(
				compiled.Program,
				definition.RowCount,
				definition.ChannelCount),
			compiled.Diagnostics);
	}

	/// <summary>
	/// Compile a separate, invocation-local streaming Roslyn Pattern program.
	/// Raw note positions are nondecreasing among accepted events. Earlier
	/// emissions are silently discarded; equal-row emissions remain in order.
	/// This API is not used by production song compilation or the recursive
	/// timeline until CPU checkpoint scheduling and full parity are ready.
	/// </summary>
	public static ScriptCompilationResult<IIncrementalRawPatternNoteGenerator>
		CompileIncrementalPattern(ScriptPatternDefinition definition,
		bool instrumentActionCallsites = false)
	{
		ArgumentNullException.ThrowIfNull(definition);

		// Establish exact helper symbols on the unrewritten source before
		// inserting either trusted F6 callsites or streaming yields. A local
		// delegate called Note/Tempo is not a musical event generator.
		IReadOnlyList<ScriptAnalysisDiagnostic> authored =
			AnalyzePatternSource(definition.Source);
		if (HasErrors(authored))
			return new(null, authored);
		HashSet<int> emitterStarts = ResolveAuthoredCallStarts(
			definition.Source, ScriptWrapperKind.Pattern);

		IReadOnlyList<ScriptAnalysisDiagnostic> restrictions =
			ValidateIncrementalPatternStatements(definition.Source, emitterStarts);
		if (restrictions.Count != 0)
			return new(null, restrictions);

		string className =
			"__HeresyIncrementalPattern_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			definition.Source,
			className,
			BuildIncrementalPatternWrapper(
				className,
				InstrumentIncrementalPattern(definition.Source,
					emitterStarts, instrumentActionCallsites)),
			typeof(PatternScriptProgram),
			allowInjectedPlayCallsites: instrumentActionCallsites,
			incrementalPattern: true);

		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);

		return new(
			new CompiledIncrementalPatternGenerator(
				compiled.Program,
				definition.RowCount,
				definition.ChannelCount),
			compiled.Diagnostics);
	}

	/// <summary>
	/// Compile a per-invocation, on-demand Sequence entry lookup.
	/// Both the eager and incremental processors use the same script
	/// function and never buffer a generated list of orders.
	/// </summary>
	public static ScriptCompilationResult<ISequenceEntrySourceFactory>
		CompileIncrementalSequence(ScriptSequenceDefinition definition,
		bool instrumentPlayCallsites = false)
	{
		ArgumentNullException.ThrowIfNull(definition);
		// Author-written code is always checked in its canonical form,
		// before any trusted metadata constructors are generated.
		if (instrumentPlayCallsites)
		{
			IReadOnlyList<ScriptAnalysisDiagnostic> authored =
				AnalyzeSequenceSource(definition.Source);
			if (HasErrors(authored))
				return new(null, authored);
		}

		string className = "__HeresySequenceLookup_" + Guid.NewGuid().ToString("N");
		string source = instrumentPlayCallsites
			? InstrumentSequencePlayCallsites(definition.Source,
				ResolveAuthoredCallStarts(
					definition.Source, ScriptWrapperKind.Sequence))
			: definition.Source;
		ScriptCompilationResult<Type> compiled = CompileType(
			definition.Source, className,
			BuildSequenceWrapper(className, InstrumentLoops(source)),
			typeof(SequenceScriptProgram),
			allowInjectedPlayCallsites: instrumentPlayCallsites);

		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);
		return new(new CompiledSequenceEntryFactory(compiled.Program),
			compiled.Diagnostics);
	}

	public static ScriptCompilationResult<INoteSequencer> CompileSequence(
		ScriptSequenceDefinition definition,
		ISequencePatternResolver resolver,
		int startOrder = 0,
		int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(resolver);
		if (startOrder < 0)
			throw new ArgumentOutOfRangeException(nameof(startOrder));
		if (startRow.HasValue && startRow.Value < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		string className =
			"__HeresySequence_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled =
			CompileType(
				definition.Source,
				className,
				BuildSequenceWrapper(
					className,
					InstrumentLoops(definition.Source)),
				typeof(SequenceScriptProgram));

		if (!compiled.Success || compiled.Program is null)
		{
			return new ScriptCompilationResult<INoteSequencer>(
				null,
				compiled.Diagnostics);
		}

		return new(
			new CompiledSequenceSequencer(
				compiled.Program,
				resolver,
				startOrder,
				startRow,
				shouldFollowOrderJump),
			compiled.Diagnostics);
	}

	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeInstrumentPitchSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(source);
		if (shape.Count != 0)
			return shape;
		return PrepareCompilation(source,
			BuildInstrumentPitchWrapper("__HeresyPitch_Analysis", source)).Diagnostics;
	}

	public static ScriptCompilationResult<IScriptInstrumentPitchEvaluator>
		CompileInstrumentPitch(ScriptInstrumentDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(definition.PitchExpression);
		if (shape.Count != 0)
			return new(null, shape);
		string className =
			"__HeresyInstrumentPitch_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			definition.PitchExpression, className,
			BuildInstrumentPitchWrapper(className, definition.PitchExpression),
			typeof(IScriptInstrumentPitchEvaluator));
		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);
		return new(
			Activator.CreateInstance(compiled.Program)
				as IScriptInstrumentPitchEvaluator
					?? throw new InvalidOperationException(
						"Could not construct compiled Instrument pitch evaluator."),
			compiled.Diagnostics);
	}

    /// <summary>Compile a pure, restricted expression returning Inherit or
    /// a concrete action from the same incoming pitch used by SelectToneIndex.
    /// Compilation is completed before playback; no dynamic script execution
    /// can touch mutable Instrument authoring state during PCM.</summary>
    public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeInstrumentNnaSource(
        string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var shape = ValidateEnvelopeExpression(source);
        if (shape.Count != 0) return shape;
        return PrepareCompilation(source,
            BuildInstrumentNnaWrapper("__HeresyNna_Analysis", source)).Diagnostics;
    }

    public static ScriptCompilationResult<IScriptInstrumentNnaEvaluator>
        CompileInstrumentNna(ScriptInstrumentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var shape = ValidateEnvelopeExpression(definition.NnaExpression);
        if (shape.Count != 0) return new(null, shape);
        string className = "__HeresyInstrumentNna_" + Guid.NewGuid().ToString("N");
        ScriptCompilationResult<Type> compiled = CompileType(
            definition.NnaExpression, className,
            BuildInstrumentNnaWrapper(className, definition.NnaExpression),
            typeof(IScriptInstrumentNnaEvaluator));
        if (!compiled.Success || compiled.Program is null)
            return new(null, compiled.Diagnostics);
        return new(
            Activator.CreateInstance(compiled.Program)
                as IScriptInstrumentNnaEvaluator
                ?? throw new InvalidOperationException(
                    "Could not construct compiled Instrument NNA evaluator."),
            compiled.Diagnostics);
    }

	/// <summary>
	/// Compile a deterministic per-invocation tone index expression. Selection
	/// is restricted to declared ToneSpecifications: -1 means silence.
	/// </summary>
	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeInstrumentSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(source);
		if (shape.Count != 0)
			return shape;
		return PrepareCompilation(source,
			BuildInstrumentWrapper("__HeresyInstrument_Analysis", source)).Diagnostics;
	}

	public static ScriptCompilationResult<IScriptInstrumentToneEvaluator>
		CompileInstrument(ScriptInstrumentDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(definition.Source);
		if (shape.Count != 0)
			return new(null, shape);
		string className =
			"__HeresyInstrument_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			definition.Source, className,
			BuildInstrumentWrapper(className, definition.Source),
			typeof(IScriptInstrumentToneEvaluator));
		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);
		return new(
			Activator.CreateInstance(compiled.Program)
				as IScriptInstrumentToneEvaluator
					?? throw new InvalidOperationException(
						"Could not construct a compiled Instrument selector."),
			compiled.Diagnostics);
	}

	/// <summary>
	/// Compile a bounded stateless absolute-delta expression once per snapshot.
	/// Native duration always uses wall time; rowTime is an optional
	/// continuous tracker-tick coordinate from the effect's start.
	/// </summary>
	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeParametricSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(source);
		if (shape.Count != 0)
			return shape;
		return PrepareCompilation(source,
			BuildParametricWrapper("__Parametric_Analysis", source)).Diagnostics;
	}

	public static ScriptCompilationResult<IScriptParametricEvaluator>
		CompileParametric(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(source);
		if (shape.Count != 0)
			return new(null, shape);
		string className = "__Parametric_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			source, className, BuildParametricWrapper(className, source),
			typeof(IScriptParametricEvaluator));
		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);
		return new(
			Activator.CreateInstance(compiled.Program)
				as IScriptParametricEvaluator
					?? throw new InvalidOperationException(
						"Cannot construct parametric evaluator."),
			compiled.Diagnostics);
	}

	/// <summary>
	/// Validate one scalar expression without generating a persistent script
	/// assembly. Time inputs are in seconds of active (unpaused) envelope
	/// time. No loops, object allocations, I/O or arbitrary framework access.
	/// </summary>
	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzeEnvelopeSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(source);
		if (shape.Count != 0)
			return shape;
		return PrepareCompilation(source,
			BuildEnvelopeWrapper("__HeresyEnvelope_Analysis", source)).Diagnostics;
	}

	public static ScriptCompilationResult<IScriptEnvelopeEvaluator> CompileEnvelope(
		ScriptEnvelopeDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		IReadOnlyList<ScriptAnalysisDiagnostic> shape =
			ValidateEnvelopeExpression(definition.Source);
		if (shape.Count != 0)
			return new(null, shape);
		string className =
			"__HeresyEnvelope_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			definition.Source, className,
			BuildEnvelopeWrapper(className, definition.Source),
			typeof(IScriptEnvelopeEvaluator));
		if (!compiled.Success || compiled.Program is null)
			return new(null, compiled.Diagnostics);
		return new(
			Activator.CreateInstance(compiled.Program)
				as IScriptEnvelopeEvaluator
					?? throw new InvalidOperationException(
						"Could not construct a compiled Envelope evaluator."),
			compiled.Diagnostics);
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateEnvelopeExpression(string source)
	{
		if (string.IsNullOrWhiteSpace(source)
			|| source.Length > 2048)
		{
			return [new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode, ScriptDiagnosticSeverity.Error,
				"An Envelope must contain one expression of at most 2048 characters.",
				new ScriptSourceSpan(0, source.Length))];
		}
		ExpressionSyntax expression = SyntaxFactory.ParseExpression(source);
		if (expression.ContainsDiagnostics
			|| expression.DescendantTrivia(descendIntoTrivia: true)
				.Any(trivia => trivia.IsKind(SyntaxKind.SkippedTokensTrivia))
			|| expression.DescendantNodesAndSelf().Count() > 256)
		{
			return [new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode, ScriptDiagnosticSeverity.Error,
				"An Envelope script must be a single bounded C# expression.",
				new ScriptSourceSpan(0, source.Length))];
		}
		return [];
	}

	private static ScriptCompilationResult<Type> CompileType(
		string source,
		string className,
		string wrapperSource,
		Type requiredBaseType,
		bool allowInjectedPlayCallsites = false,
		bool incrementalPattern = false)
	{
		ScriptWrapperKind wrapperKind = requiredBaseType == typeof(PatternScriptProgram)
			? incrementalPattern ? ScriptWrapperKind.IncrementalPattern
				: ScriptWrapperKind.Pattern
			: requiredBaseType == typeof(SequenceScriptProgram)
				? ScriptWrapperKind.Sequence
				: ScriptWrapperKind.None;
		PreparedScriptCompilation prepared =
			PrepareCompilation(
				source,
				wrapperSource,
				allowInjectedPlayCallsites,
				wrapperKind);
		List<ScriptAnalysisDiagnostic> diagnostics =
			[.. prepared.Diagnostics];

		if (prepared.Compilation is null)
			return new(null, diagnostics);

		CSharpCompilation compilation =
			prepared.Compilation;

		using MemoryStream image = new();
		var emitResult = compilation.Emit(image);
		foreach (Diagnostic diagnostic in emitResult.Diagnostics)
		{
			if (diagnostic.Severity
				is DiagnosticSeverity.Warning
					or DiagnosticSeverity.Error)
			{
				ScriptAnalysisDiagnostic projected =
					ProjectDiagnostic(
						diagnostic,
						source);
				if (!diagnostics.Contains(projected))
					diagnostics.Add(projected);
			}
		}

		if (!emitResult.Success || HasErrors(diagnostics))
			return new(null, diagnostics);

		Assembly assembly =
			Assembly.Load(image.ToArray());
		Type? generatedType =
			assembly.GetType(
				className,
				throwOnError: false,
				ignoreCase: false);

		if (generatedType is null
			|| !requiredBaseType.IsAssignableFrom(generatedType))
		{
			diagnostics.Add(
				new ScriptAnalysisDiagnostic(
					RestrictedFeatureCode,
					ScriptDiagnosticSeverity.Error,
					"The generated script type did not satisfy the expected runtime contract.",
					new ScriptSourceSpan(0, 0)));
			return new(null, diagnostics);
		}

		return new(generatedType, diagnostics);
	}

	private static PreparedScriptCompilation PrepareCompilation(
		string source,
		string wrapperSource,
		bool allowInjectedPlayCallsites = false,
		ScriptWrapperKind wrapperKind = ScriptWrapperKind.None)
	{
		ScriptReferenceAnalysis referenceAnalysis =
			ScriptReferenceAnalyzer.Analyze(source);
		List<ScriptAnalysisDiagnostic> diagnostics =
			[.. referenceAnalysis.Diagnostics];

		if (!referenceAnalysis.IsReliable)
			return new(null, diagnostics);

		diagnostics.AddRange(ValidateRestrictedSyntax(
			source, permitManagedData: wrapperKind != ScriptWrapperKind.None));
		if (HasErrors(diagnostics))
			return new(null, diagnostics);

		// Validate only ORIGINAL user statements, before loop checkpoints,
		// incremental yields, or F6 SequenceCallsite constructors are injected.
		// This canonical gate is the Pattern/Sequence author-facing
		// authorization check. The legacy semantic checker remains mandatory
		// only in conservative Instrument/Envelope/parametric contexts.
		if (wrapperKind != ScriptWrapperKind.None)
		{
			diagnostics.AddRange(ValidateCanonicalAuthoredOperations(
				source, wrapperKind));
			if (HasErrors(diagnostics))
				return new(null, diagnostics);
		}

		SyntaxTree syntaxTree =
			CSharpSyntaxTree.ParseText(
				wrapperSource,
				WrapperParseOptions);

		CSharpCompilation compilation =
			CSharpCompilation.Create(
				"Heresy.GeneratedScript."
					+ Guid.NewGuid().ToString("N"),
				[syntaxTree],
				MetadataReferences,
				new CSharpCompilationOptions(
					OutputKind.DynamicallyLinkedLibrary,
					optimizationLevel: OptimizationLevel.Release,
					allowUnsafe: false,
					checkOverflow: true));

		foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
		{
			if (diagnostic.Severity
				is DiagnosticSeverity.Warning
					or DiagnosticSeverity.Error)
			{
				diagnostics.Add(
					ProjectDiagnostic(
						diagnostic,
						source));
			}
		}

		if (!HasErrors(diagnostics) && wrapperKind != ScriptWrapperKind.None)
		{
			// This is a structural trust-boundary check, NOT yet the new
			// runtime API policy. In particular, loop checkpoints and F6
			// callsite metadata are trusted generated code even when #line
			// maps them into the authored source region.
			foreach (ScriptWrapperBoundaryViolation violation
				in ScriptAuthoredWrapperBoundary.Validate(syntaxTree, wrapperKind))
			{
				AddRestrictedDiagnostic(diagnostics,
					violation.Location is null
						? new ScriptSourceSpan(0, 0)
						: MapSpan(violation.Location, source),
					violation.Reason);
			}
		}

		if (HasErrors(diagnostics))
			return new(null, diagnostics);

		// The canonical bound-operation pass has checked the exact original
		// Pattern/Sequence statements. Its strict host-member rule replaces
		// the old name-only validator for those two contexts. Rechecking
		// rewritten syntax would confuse Heresy-injected calls with authored
		// code. Legacy expression-script semantics remain unchanged.
		if (wrapperKind == ScriptWrapperKind.None)
		{
			diagnostics.AddRange(
				ValidateRestrictedSemantics(
					compilation,
					syntaxTree,
					source,
					allowInjectedPlayCallsites));
		}
		if (HasErrors(diagnostics))
			return new(null, diagnostics);

		return new(compilation, diagnostics);
	}


	/// <summary>
	/// Resolve and audit the actual authored Pattern/Sequence statements in a
	/// clean wrapper. Instrumentation may insert trusted members *within* the
	/// #line region, so inspecting the rewritten wrapper is not sufficient to
	/// distinguish user capabilities from compiler-generated operations.
	/// Exact helper/member permissions are verified by this canonical gate;
	/// generated rewrites are checked for wrapper structure, not audited as
	/// if their trusted injected operations were authored.
	/// </summary>
	internal static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateCanonicalAuthoredOperations(
			string source, ScriptWrapperKind wrapperKind)
	{
		ScriptWrapperKind canonicalKind =
			wrapperKind == ScriptWrapperKind.Sequence
				? ScriptWrapperKind.Sequence : ScriptWrapperKind.Pattern;
		string entryName = canonicalKind == ScriptWrapperKind.Sequence
			? "ExecuteEntry" : "ExecuteScript";
		string wrapper = canonicalKind == ScriptWrapperKind.Sequence
			? BuildSequenceWrapper("__HeresyCanonicalSequence", source)
			: BuildPatternWrapper("__HeresyCanonicalPattern", source);

		SyntaxTree tree = CSharpSyntaxTree.ParseText(
			wrapper, WrapperParseOptions);
		List<ScriptAnalysisDiagnostic> diagnostics = [];

		foreach (ScriptWrapperBoundaryViolation violation
			in ScriptAuthoredWrapperBoundary.Validate(tree, canonicalKind))
		{
			AddRestrictedDiagnostic(diagnostics,
				violation.Location is null
					? new ScriptSourceSpan(0, 0)
					: MapSpan(violation.Location, source),
				violation.Reason);
		}
		if (HasErrors(diagnostics))
			return diagnostics;

		CSharpCompilation canonical = CSharpCompilation.Create(
			"Heresy.CanonicalScript." + Guid.NewGuid().ToString("N"),
			[tree], MetadataReferences,
			new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel: OptimizationLevel.Release,
				allowUnsafe: false, checkOverflow: true));

		foreach (Diagnostic diagnostic in canonical.GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error)
				diagnostics.Add(ProjectDiagnostic(diagnostic, source));
		}
		if (HasErrors(diagnostics))
			return diagnostics;

		SemanticModel model = canonical.GetSemanticModel(tree);
		MethodDeclarationSyntax? entry = tree.GetRoot().DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(method =>
				method.Identifier.ValueText == entryName);
		if (entry?.Body is null)
		{
			AddRestrictedDiagnostic(diagnostics,
				new ScriptSourceSpan(0, 0),
				"Canonical script entry method could not be resolved.");
			return diagnostics;
		}

		INamedTypeSymbol? patternBase = canonical.GetTypeByMetadataName(
			typeof(PatternScriptProgram).FullName!);
		INamedTypeSymbol? sequenceBase = canonical.GetTypeByMetadataName(
			typeof(SequenceScriptProgram).FullName!);
		INamedTypeSymbol? mathType =
			canonical.GetTypeByMetadataName("System.Math");
		INamedTypeSymbol? pitchMath = canonical.GetTypeByMetadataName(
			typeof(ScriptPitchMath).FullName!);
		INamedTypeSymbol? displacement = canonical.GetTypeByMetadataName(
			typeof(NoteDisplacementAction).FullName!);
		INamedTypeSymbol? past = canonical.GetTypeByMetadataName(
			typeof(TrackerPastNoteAction).FullName!);
		INamedTypeSymbol? nna = canonical.GetTypeByMetadataName(
			typeof(InstrumentNnaAction).FullName!);

		// These Heresy-owned value types are intentionally opaque. The
		// canonical verifier checks every authored member against the exact
		// permitted helper list, rejects other host/generated members, and
		// blocks inherited protected host-object operations. The type
		// exception alone never authorizes calls on musical objects.
		IAssemblySymbol? coreAssembly = canonical.GetTypeByMetadataName(
			typeof(SequencingContext).FullName!)?.ContainingAssembly;
		IAssemblySymbol? scriptAssembly = patternBase?.ContainingAssembly;
		bool IsOpaqueHeresyType(ITypeSymbol type)
		{
			if (type is INamedTypeSymbol named
				&& named.OriginalDefinition.SpecialType
					== SpecialType.System_Nullable_T
				&& named.TypeArguments.Length == 1)
				return IsOpaqueHeresyType(named.TypeArguments[0]);

			return SymbolEqualityComparer.Default.Equals(
					type.ContainingAssembly, coreAssembly)
				|| SymbolEqualityComparer.Default.Equals(
					type.ContainingAssembly, scriptAssembly);
		}
		bool IsApprovedHeresyMember(ISymbol symbol) => symbol switch
		{
			IMethodSymbol method => IsAllowedInvocation(
				method, patternBase, sequenceBase, mathType, pitchMath,
				allowInjectedPlayCallsites: false),
			_ => IsAllowedActionEnumMember(
				symbol, displacement, past, nna),
		};

		foreach (StatementSyntax statement in entry.Body.Statements)
		{
			if (!IsScriptLocation(statement.GetLocation()))
				continue; // trusted Sequence fallthrough after #line default

			foreach (ScriptRuntimeOperationFinding finding
				in ScriptRuntimeOperationVerifier.AnalyzeCanonical(
					model, statement,
					IsApprovedHeresyMember, IsOpaqueHeresyType))
			{
				Location location = Location.Create(tree, finding.Span);
				AddRestrictedDiagnostic(diagnostics,
					IsScriptLocation(location)
						? MapSpan(location, source)
						: new ScriptSourceSpan(0, 0),
					finding.Reason);
			}
		}

		return diagnostics;
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateRestrictedSyntax(string source, bool permitManagedData = false)
	{
		SyntaxTree tree =
			CSharpSyntaxTree.ParseText(
				source,
				ScriptParseOptions);
		SyntaxNode root = tree.GetRoot();
		List<ScriptAnalysisDiagnostic> diagnostics = [];

		foreach (SyntaxTrivia trivia
			in root.DescendantTrivia(descendIntoTrivia: true))
		{
			if (trivia.IsDirective)
			{
				AddRestrictedDiagnostic(
					diagnostics,
					trivia.Span,
					"Preprocessor directives are not available in Heresy scripts.");
			}
		}

		foreach (SyntaxNode node in root.DescendantNodesAndSelf())
		{
			string? reason =
				node switch
				{
					LocalFunctionStatementSyntax =>
						"Local functions are not available in Heresy scripts.",
					AnonymousFunctionExpressionSyntax when !permitManagedData =>
						"Lambdas and anonymous functions are not available in Heresy scripts.",
					ObjectCreationExpressionSyntax
						or ImplicitObjectCreationExpressionSyntax
						when !permitManagedData =>
						"Object creation is not available in Heresy scripts.",
					AnonymousObjectCreationExpressionSyntax =>
						"Anonymous object creation is not available in Heresy scripts.",
					ArrayCreationExpressionSyntax
						or ImplicitArrayCreationExpressionSyntax
						when !permitManagedData =>
						"Array allocation is not available in Heresy scripts.",
					StackAllocArrayCreationExpressionSyntax =>
						"Stack allocation is not available in Heresy scripts.",
					AwaitExpressionSyntax =>
						"Async execution is not available in Heresy scripts.",
					ThrowExpressionSyntax
						or ThrowStatementSyntax
						or TryStatementSyntax =>
						"Exception-control constructs are not available in Heresy scripts.",
					LockStatementSyntax =>
						"Locking is not available in Heresy scripts.",
					UnsafeStatementSyntax
						or FixedStatementSyntax =>
						"Unsafe execution is not available in Heresy scripts.",
					YieldStatementSyntax =>
						"Iterator blocks are not available in Heresy scripts.",
					GotoStatementSyntax
						or LabeledStatementSyntax =>
						"Goto control flow is not available in Heresy scripts.",
					ForEachStatementSyntax foreachStatement
						when foreachStatement.AwaitKeyword.RawKind != 0 =>
						"Asynchronous foreach is not available in Heresy scripts.",
					ForEachStatementSyntax when !permitManagedData =>
						"foreach is not available in the initial restricted scripting surface.",
					ForEachVariableStatementSyntax =>
						"Deconstructing foreach is not yet available in Heresy scripts.",
					QueryExpressionSyntax =>
						"LINQ query syntax is not available in Heresy scripts.",
					TypeOfExpressionSyntax =>
						"Runtime type access is not available in Heresy scripts.",
					ConditionalAccessExpressionSyntax =>
						"Conditional member access is not available in the restricted scripting surface.",
					ThisExpressionSyntax
						or BaseExpressionSyntax =>
						"Direct access to the generated script object is not available.",
					InterpolatedStringExpressionSyntax when !permitManagedData =>
						"String construction is not available in Heresy scripts.",
					LiteralExpressionSyntax literal
						when !permitManagedData && literal.IsKind(
							SyntaxKind.StringLiteralExpression) =>
						"String values are not available in Heresy scripts.",
					UsingStatementSyntax when !permitManagedData =>
						"using statements are not available in Heresy scripts.",
					LocalDeclarationStatementSyntax declaration
						when !permitManagedData
							&& declaration.UsingKeyword.RawKind != 0 =>
						"using declarations are not available in Heresy scripts.",
					_ => null,
				};

			if (reason is not null)
			{
				AddRestrictedDiagnostic(
					diagnostics,
					node.Span,
					reason);
			}
		}

		return diagnostics;
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateRestrictedSemantics(
			CSharpCompilation compilation,
			SyntaxTree syntaxTree,
			string source,
			bool allowInjectedPlayCallsites)
	{
		SemanticModel semanticModel =
			compilation.GetSemanticModel(
				syntaxTree,
				ignoreAccessibility: false);
		SyntaxNode root = syntaxTree.GetRoot();
		INamedTypeSymbol? patternBase =
			compilation.GetTypeByMetadataName(
				typeof(PatternScriptProgram).FullName
					?? throw new InvalidOperationException());
		INamedTypeSymbol? sequenceBase =
			compilation.GetTypeByMetadataName(
				typeof(SequenceScriptProgram).FullName
					?? throw new InvalidOperationException());
		INamedTypeSymbol? mathType =
			compilation.GetTypeByMetadataName("System.Math");
		INamedTypeSymbol? pitchMathType =
			compilation.GetTypeByMetadataName(
				typeof(ScriptPitchMath).FullName
					?? throw new InvalidOperationException());
		INamedTypeSymbol? displacementType =
			compilation.GetTypeByMetadataName(
				typeof(NoteDisplacementAction).FullName
					?? throw new InvalidOperationException());
		INamedTypeSymbol? pastNoteType =
            compilation.GetTypeByMetadataName(
                typeof(TrackerPastNoteAction).FullName
                    ?? throw new InvalidOperationException());
        INamedTypeSymbol? instrumentNnaType =
            compilation.GetTypeByMetadataName(
                typeof(InstrumentNnaAction).FullName
                    ?? throw new InvalidOperationException());

		List<ScriptAnalysisDiagnostic> diagnostics = [];

		foreach (InvocationExpressionSyntax invocation
			in root.DescendantNodes()
				.OfType<InvocationExpressionSyntax>())
		{
			if (!IsScriptLocation(invocation.GetLocation()))
				continue;

			ISymbol? symbol =
				semanticModel.GetSymbolInfo(invocation).Symbol;
			if (symbol is IMethodSymbol method
				&& IsAllowedInvocation(
					method,
					patternBase,
					sequenceBase,
					mathType,
					pitchMathType,
					allowInjectedPlayCallsites))
			{
				continue;
			}

			AddRestrictedDiagnostic(
				diagnostics,
				MapSpan(
					invocation.GetLocation(),
					source),
				"Only Heresy script helpers and System.Math calls are allowed.");
		}

		foreach (MemberAccessExpressionSyntax access
			in root.DescendantNodes()
				.OfType<MemberAccessExpressionSyntax>())
		{
			if (!IsScriptLocation(access.GetLocation()))
				continue;

			ISymbol? symbol =
				semanticModel.GetSymbolInfo(access).Symbol;
			if (IsAllowedMathMember(symbol, mathType)
				|| IsAllowedActionEnumMember(symbol,
                    displacementType, pastNoteType, instrumentNnaType))
				continue;

			AddRestrictedDiagnostic(
				diagnostics,
				MapSpan(
					access.GetLocation(),
					source),
				"Framework and object member access is not available in Heresy scripts.");
		}

		return diagnostics;
	}

	private static bool IsAllowedInvocation(
		IMethodSymbol method,
		INamedTypeSymbol? patternBase,
		INamedTypeSymbol? sequenceBase,
		INamedTypeSymbol? mathType,
		INamedTypeSymbol? pitchMathType,
		bool allowInjectedPlayCallsites)
	{
		// Authors cannot opt themselves into a generated-only entrypoint
		// even though the protected overload exists on the runtime base.
		// Editor compilation first checks original authored code under this
		// rule, then checks the trusted rewritten wrapper with opt-in.
		if (method.Parameters.Length > 0
			&& method.Parameters[0].Type.Name == "SequenceCallsite"
			&& (method.Name is "Play" or "Note" or "NoteVirtual"
				or "NoteFilter" or "NoteFilterVirtual"
				or "ChannelFilter" or "ChannelFilterVirtual"
				or "Off" or "Cut" or "OffVirtual" or "CutVirtual"
				or "OffVirtualInScope" or "CutVirtualInScope"
				or "OffAllVirtual" or "CutAllVirtual"
				or "Tempo" or "Speed" or "Seek" or "Pan" or "Surround"
				or "Filter" or "FilterCutoff" or "FilterResonance"
				or "VolumeVirtual" or "SourceFrameVirtual"
				or "DisplacementVirtual" or "PastVirtual")
			&& !allowInjectedPlayCallsites)
			return false;

		INamedTypeSymbol containingType = method.ContainingType;
		if (SymbolEqualityComparer.Default.Equals(containingType, patternBase))
			return method.MethodKind == MethodKind.Ordinary
				&& (method.Name is "_O" or "Random" or "Checkpoint"
					|| ScriptPatternEmissionPolicy.IsEmitterName(method.Name));
		if (SymbolEqualityComparer.Default.Equals(containingType, sequenceBase))
			return method.MethodKind == MethodKind.Ordinary
				&& method.Name is "_O" or "Random" or "Checkpoint" or "Play";
		return SymbolEqualityComparer.Default.Equals(containingType, mathType)
			|| IsApprovedPitchMathMethod(method, pitchMathType);
	}

	/// <summary>
	/// Only these four numerical conversions are callable from scripts.
	/// Do not implicitly allow all public methods on the shared utility type.
	/// </summary>
	private static bool IsApprovedPitchMathMethod(
		IMethodSymbol method, INamedTypeSymbol? pitchMathType)
		=> pitchMathType is not null
			&& SymbolEqualityComparer.Default.Equals(
					method.ContainingType, pitchMathType)
			&& method.IsStatic
			&& method.Name is
				("Semitone" or "Cent" or "Octave" or "SemitoneOffset")
			&& method.Parameters.Length == 1
			&& method.Parameters[0].Type.SpecialType
				== SpecialType.System_Double
			&& method.ReturnType.SpecialType
				== SpecialType.System_Double;

	private static bool IsAllowedMathMember(
		ISymbol? symbol,
		INamedTypeSymbol? mathType)
	{
		if (symbol is null || mathType is null)
			return false;

		if (symbol is INamedTypeSymbol namedType)
		{
			return SymbolEqualityComparer.Default.Equals(
				namedType,
				mathType);
		}

		return symbol.ContainingType is not null
			&& SymbolEqualityComparer.Default.Equals(
				symbol.ContainingType,
				mathType);
	}

	/// <summary>
	/// Expose only literal fields of the two strongly typed voice-action enums,
	/// not arbitrary enum/framework member access from restricted C# scripts.
	/// </summary>
	private static bool IsAllowedActionEnumMember(
		ISymbol? symbol,
		INamedTypeSymbol? displacementType,
        INamedTypeSymbol? pastNoteType,
        INamedTypeSymbol? instrumentNnaType)
		=> symbol is IFieldSymbol field
			&& field.HasConstantValue
			&& (SymbolEqualityComparer.Default.Equals(
					field.ContainingType, displacementType)
				|| SymbolEqualityComparer.Default.Equals(
					field.ContainingType, pastNoteType)
                || SymbolEqualityComparer.Default.Equals(
                    field.ContainingType, instrumentNnaType));

	private static bool IsScriptLocation(Location location)
	{
		FileLinePositionSpan mapped =
			location.GetMappedLineSpan();
		return mapped.IsValid
			&& string.Equals(
				mapped.Path,
				ScriptPath,
				StringComparison.Ordinal);
	}

	private static string InstrumentLoops(string source)
	{
		SyntaxTree tree =
			CSharpSyntaxTree.ParseText(
				source,
				ScriptParseOptions);
		SyntaxNode root = tree.GetRoot();
		return new LoopCheckpointRewriter()
			.Visit(root)
			?.ToFullString()
			?? source;
	}

	/// <summary>
	/// Bind original authored calls in an unmodified wrapper and retain their
	/// source positions. Name matching is insufficient: local variables and
	/// approved delegates may shadow Heresy helper methods, especially when
	/// a nested expression is later rewritten for incremental/F6 playback.
	/// This runs after the canonical API check, which has already rejected
	/// unresolved source and unauthorized helper access.
	/// </summary>
	private static HashSet<int> ResolveAuthoredCallStarts(
		string source, ScriptWrapperKind kind)
	{
		bool sequence = kind == ScriptWrapperKind.Sequence;
		string wrapper = sequence
			? BuildSequenceWrapper("__HeresySequenceCallResolution", source)
			: BuildPatternWrapper("__HeresyPatternCallResolution", source);
		SyntaxTree tree = CSharpSyntaxTree.ParseText(wrapper, WrapperParseOptions);
		CSharpCompilation compilation = CSharpCompilation.Create(
			"Heresy.ScriptCallResolution." + Guid.NewGuid().ToString("N"),
			[tree], MetadataReferences,
			new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel: OptimizationLevel.Release,
				allowUnsafe: false, checkOverflow: true));
		SemanticModel semantic = compilation.GetSemanticModel(tree);
		INamedTypeSymbol? sequenceBase = compilation.GetTypeByMetadataName(
			typeof(SequenceScriptProgram).FullName!);
		HashSet<int> positions = [];
		foreach (InvocationExpressionSyntax invocation
			in tree.GetRoot().DescendantNodes()
				.OfType<InvocationExpressionSyntax>())
		{
			if (!IsScriptLocation(invocation.GetLocation())
				|| semantic.GetSymbolInfo(invocation).Symbol is not
					IMethodSymbol method)
				continue;
			bool trusted = sequence
				? SymbolEqualityComparer.Default.Equals(
					method.ContainingType, sequenceBase)
					&& method.Name == "Play"
					&& method.Parameters.Length > 0
					&& method.Parameters[0].Type.Name != "SequenceCallsite"
				: ScriptPatternEmissionPolicy.IsEmitterMethod(
					method, compilation)
					&& (method.Parameters.Length == 0
						|| method.Parameters[0].Type.Name != "SequenceCallsite");
			if (trusted)
				positions.Add(MapSpan(invocation.GetLocation(), source).Start);
		}
		return positions;
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateIncrementalPatternStatements(
			string source, IReadOnlySet<int> emitterStarts)
	{
		SyntaxNode root = CSharpSyntaxTree.ParseText(
			source, ScriptParseOptions).GetRoot();
		List<ScriptAnalysisDiagnostic> diagnostics = [];
		foreach (InvocationExpressionSyntax invocation
			in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			if (!emitterStarts.Contains(invocation.SpanStart))
				continue;
			if (invocation.Parent is ExpressionStatementSyntax statement
				&& ReferenceEquals(statement.Expression, invocation))
				continue;
			diagnostics.Add(new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode, ScriptDiagnosticSeverity.Error,
				"Streaming note helpers must be direct statements.",
				new ScriptSourceSpan(invocation.Span.Start, invocation.Span.Length)));
		}
		return diagnostics;
	}

	/// <summary>Only the editor-F6 Sequence compiler uses this pass.
	/// Tokens use offsets from the ORIGINAL author source, never the
	/// loop-checkpoint-expanded source. The ordinary compiler never runs it.</summary>
	private static string InstrumentSequencePlayCallsites(
		string source, IReadOnlySet<int> playCallStarts)
	{
		SyntaxNode root = CSharpSyntaxTree.ParseText(
			source, ScriptParseOptions).GetRoot();
		return new SequencePlayCallsiteRewriter(playCallStarts).Visit(root)
			?.ToFullString() ?? source;
	}

	private sealed class SequencePlayCallsiteRewriter(
		IReadOnlySet<int> playCallStarts) : CSharpSyntaxRewriter
	{
		public override SyntaxNode? VisitInvocationExpression(
			InvocationExpressionSyntax node)
		{
			InvocationExpressionSyntax rewritten =
				(InvocationExpressionSyntax)(base.VisitInvocationExpression(node)
					?? node);
			if (!playCallStarts.Contains(node.SpanStart))
				return rewritten;

			// The user wrote Play(x[, row]); capture the *entire* original
			// invocation, including '(' and ')' and any multiline arguments,
			// before adding the trusted leading metadata parameter. Preserve
			// the authored offset so _O() visual tokens map correctly.
			int start = node.SpanStart;
			int length = node.Span.Length;
			ArgumentSyntax callsite = SyntaxFactory.Argument(
				SyntaxFactory.ParseExpression(
					$"new SequenceCallsite({start}, {length})"));
			return rewritten.WithArgumentList(rewritten.ArgumentList.WithArguments(
				rewritten.ArgumentList.Arguments.Insert(0, callsite)))
				.WithTriviaFrom(node);
		}
	}

	private static string InstrumentIncrementalPattern(string source,
		IReadOnlySet<int> emitterStarts, bool instrumentActionCallsites = false)
	{
		SyntaxNode root = CSharpSyntaxTree.ParseText(
			source, ScriptParseOptions).GetRoot();
		// Rewrite direct approved emitters as a single operation, BEFORE loop
		// checkpoints alter source positions. This also ensures F6 spans use
		// the author's original invocation, not generated yields/guards.
		root = new StrictPatternEmitterRewriter(
			emitterStarts, instrumentActionCallsites).Visit(root) ?? root;
		root = new LoopCheckpointRewriter(cooperative: true).Visit(root)
			?? root;
		return root.ToFullString();
	}

	private sealed class StrictPatternEmitterRewriter(
		IReadOnlySet<int> emitterStarts, bool instrumentActionCallsites)
		: CSharpSyntaxRewriter
	{
		public override SyntaxNode? VisitExpressionStatement(
			ExpressionStatementSyntax node)
		{
			ExpressionStatementSyntax visited =
				(ExpressionStatementSyntax)(base.VisitExpressionStatement(node) ?? node);
			if (node.Expression is not InvocationExpressionSyntax invocation
				|| !emitterStarts.Contains(invocation.SpanStart))
				return visited;

			if (instrumentActionCallsites)
			{
				if (visited.Expression is not InvocationExpressionSyntax rewritten)
					throw new InvalidOperationException(
						"Bound Pattern emission lost its invocation during rewriting.");
				ArgumentSyntax callsite = SyntaxFactory.Argument(
					SyntaxFactory.ParseExpression(
						$"new SequenceCallsite({invocation.SpanStart}, {invocation.Span.Length})"));
				visited = visited.WithExpression(
					rewritten.WithArgumentList(
						rewritten.ArgumentList.WithArguments(
							rewritten.ArgumentList.Arguments.Insert(0, callsite))));
			}

			// Every approved event generator executes exactly once, followed
			// by an outer-iterator yield. Local methods/delegates with the
			// same spelling cannot acquire a bogus yield or F6 argument.
			return SyntaxFactory.Block(
				visited.WithoutLeadingTrivia(),
				SyntaxFactory.ParseStatement("yield return EmitPendingStep();"))
				.WithTriviaFrom(node);
		}
	}

	private static string BuildIncrementalPatternWrapper(
		string className, string source)
		=> $$"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using Heresy.Core.Sequences;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class {{className}} : PatternScriptProgram
			{
				public {{className}}(
					SequencingContext context,
					INoteReceiver output,
					double rowCount,
					int channelCount)
					: base(context, output, rowCount, channelCount)
				{
				}

				protected override void ExecuteScript()
					=> throw new NotSupportedException(
						"Use the incremental script enumerator.");

				protected override IEnumerable<RawPatternStep> EnumerateScript()
				{
			#line 1 "heresy-script"
			{{source}}
			#line default
					yield break;
				}
			}
			""";

	private static string BuildPatternWrapper(
		string className,
		string source)
		=> $$"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using Heresy.Core.Sequences;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class {{className}} : PatternScriptProgram
			{
				public {{className}}(
					SequencingContext context,
					INoteReceiver output,
					double rowCount,
					int channelCount)
					: base(context, output, rowCount, channelCount)
				{
				}

				protected override void ExecuteScript()
				{
			#line 1 "heresy-script"
			{{source}}
			#line default
				}
			}
			""";

	private static string BuildInstrumentPitchWrapper(
		string className, string source)
		=> """
			using System;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class __PITCH_CLASS__ : IScriptInstrumentPitchEvaluator
			{
				public double GetPitchScale(double pitchMultiplier)
				{
			#line 1 "heresy-script"
					return (__PITCH_EXPRESSION__);
			#line default
				}
			}
			"""
			.Replace("__PITCH_CLASS__", className, StringComparison.Ordinal)
			.Replace("__PITCH_EXPRESSION__", source, StringComparison.Ordinal);

    private static string BuildInstrumentNnaWrapper(string className, string source)
        => """
            using System;
            using Heresy.Core.Instruments;
            using Heresy.Scripting.Runtime;
            using static Heresy.Scripting.Runtime.ScriptPitchMath;

            public sealed class __NNA_CLASS__ : IScriptInstrumentNnaEvaluator
            {
                public InstrumentNnaAction SelectNna(double pitchMultiplier)
                {
            #line 1 "heresy-script"
                    return (__NNA_EXPRESSION__);
            #line default
                }
            }
            """
            .Replace("__NNA_CLASS__", className, StringComparison.Ordinal)
            .Replace("__NNA_EXPRESSION__", source, StringComparison.Ordinal);

	private static string BuildInstrumentWrapper(
		string className, string source)
		=> """
			using System;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class __INSTRUMENT_CLASS__ : IScriptInstrumentToneEvaluator
			{
				public int SelectToneIndex(double pitchMultiplier)
				{
			#line 1 "heresy-script"
					return (__INSTRUMENT_EXPRESSION__);
			#line default
				}
			}
			"""
			.Replace("__INSTRUMENT_CLASS__", className, StringComparison.Ordinal)
			.Replace("__INSTRUMENT_EXPRESSION__", source, StringComparison.Ordinal);

	private static string BuildParametricWrapper(string className, string source)
		=> """
			using System;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;
			public sealed class __PARAMETRIC_CLASS__ : IScriptParametricEvaluator
			{
				public double Evaluate(double elapsedSeconds, double rowTime)
				{
			#line 1 "heresy-script"
					return (__PARAMETRIC_EXPRESSION__);
			#line default
				}
			}
			"""
			.Replace("__PARAMETRIC_CLASS__", className, StringComparison.Ordinal)
			.Replace("__PARAMETRIC_EXPRESSION__", source, StringComparison.Ordinal);

	private static string BuildEnvelopeWrapper(
		string className, string source)
		=> """
			using System;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class __ENVELOPE_CLASS__ : IScriptEnvelopeEvaluator
			{
				public double Evaluate(
					double elapsedSeconds, double releaseSeconds)
				{
			#line 1 "heresy-script"
					return (__ENVELOPE_EXPRESSION__);
			#line default
				}
			}
			"""
			.Replace("__ENVELOPE_CLASS__", className, StringComparison.Ordinal)
			.Replace("__ENVELOPE_EXPRESSION__", source, StringComparison.Ordinal);

	private static string BuildSequenceWrapper(string className, string source)
		=> $$"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using Heresy.Core.Sequences;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			#nullable enable
			public sealed class {{className}} : SequenceScriptProgram
			{
				public {{className}}(SequencingContext context)
					: base(context)
				{
				}

				protected override SequenceEntry? ExecuteEntry(
					int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
				{
			#line 1 "heresy-script"
			{{source}}
			#line default
				// Script Sequence fallthrough means end of sequence.
				// A fully returning script makes this unreachable by design.
			#pragma warning disable CS0162
				return default;
			#pragma warning restore CS0162
				}
			}
			""";

	private static MetadataReference[] CreateMetadataReferences()
	{
		HashSet<string> paths =
			new(StringComparer.OrdinalIgnoreCase);

		if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
			is string trustedPlatformAssemblies)
		{
			foreach (string path
				in trustedPlatformAssemblies.Split(
					Path.PathSeparator,
					StringSplitOptions.RemoveEmptyEntries))
			{
				paths.Add(path);
			}
		}

		paths.Add(typeof(object).Assembly.Location);
		paths.Add(typeof(Enumerable).Assembly.Location);
		paths.Add(typeof(PatternScriptProgram).Assembly.Location);
		paths.Add(typeof(SequencingContext).Assembly.Location);

		return paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => MetadataReference.CreateFromFile(path))
			.ToArray();
	}

	private static ScriptAnalysisDiagnostic ProjectDiagnostic(
		Diagnostic diagnostic,
		string source)
		=> new(
			diagnostic.Id,
			diagnostic.Severity switch
			{
				DiagnosticSeverity.Error =>
					ScriptDiagnosticSeverity.Error,
				DiagnosticSeverity.Warning =>
					ScriptDiagnosticSeverity.Warning,
				_ => ScriptDiagnosticSeverity.Info,
			},
			diagnostic.GetMessage(),
			MapSpan(
				diagnostic.Location,
				source));

	private static ScriptSourceSpan MapSpan(
		Location location,
		string source)
	{
		FileLinePositionSpan mapped =
			location.GetMappedLineSpan();
		if (!mapped.IsValid
			|| !string.Equals(
				mapped.Path,
				ScriptPath,
				StringComparison.Ordinal))
		{
			return new ScriptSourceSpan(0, 0);
		}

		SourceText sourceText = SourceText.From(source);
		int start =
			MapPosition(
				sourceText,
				mapped.StartLinePosition);
		int end =
			MapPosition(
				sourceText,
				mapped.EndLinePosition);
		return new ScriptSourceSpan(
			start,
			Math.Max(0, end - start));
	}

	private static int MapPosition(
		SourceText source,
		LinePosition position)
	{
		if (position.Line < 0
			|| position.Line >= source.Lines.Count)
		{
			return 0;
		}

		TextLine line = source.Lines[position.Line];
		return Math.Min(
			line.End,
			line.Start + Math.Max(0, position.Character));
	}

	private static bool HasErrors(
		IEnumerable<ScriptAnalysisDiagnostic> diagnostics)
		=> diagnostics.Any(
			diagnostic =>
				diagnostic.Severity
					== ScriptDiagnosticSeverity.Error);

	private static void AddRestrictedDiagnostic(
		ICollection<ScriptAnalysisDiagnostic> diagnostics,
		Microsoft.CodeAnalysis.Text.TextSpan span,
		string message)
		=> diagnostics.Add(
			new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode,
				ScriptDiagnosticSeverity.Error,
				message,
				new ScriptSourceSpan(
					span.Start,
					span.Length)));

	private static void AddRestrictedDiagnostic(
		ICollection<ScriptAnalysisDiagnostic> diagnostics,
		ScriptSourceSpan span,
		string message)
		=> diagnostics.Add(
			new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode,
				ScriptDiagnosticSeverity.Error,
				message,
				span));

	private sealed record PreparedScriptCompilation(
		CSharpCompilation? Compilation,
		IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics);

	private sealed class LoopCheckpointRewriter(bool cooperative = false)
		: CSharpSyntaxRewriter
	{
		// Yield-bearing cooperative checkpoints are valid only inside the
		// generated Pattern iterator. Lambdas may contain their own loops,
		// but cannot yield steps from that iterator. Checkpoint synchronously
		// inside lambda bodies instead, keeping runaway-work protection.
		public override SyntaxNode? VisitSimpleLambdaExpression(
			SimpleLambdaExpressionSyntax node)
			=> !cooperative
				? base.VisitSimpleLambdaExpression(node)
				: node.WithBody((CSharpSyntaxNode)(
					new LoopCheckpointRewriter().Visit(node.Body) ?? node.Body));

		public override SyntaxNode? VisitParenthesizedLambdaExpression(
			ParenthesizedLambdaExpressionSyntax node)
			=> !cooperative
				? base.VisitParenthesizedLambdaExpression(node)
				: node.WithBody((CSharpSyntaxNode)(
					new LoopCheckpointRewriter().Visit(node.Body) ?? node.Body));

		public override SyntaxNode? VisitAnonymousMethodExpression(
			AnonymousMethodExpressionSyntax node)
			=> !cooperative
				? base.VisitAnonymousMethodExpression(node)
				: node.WithBlock((BlockSyntax)(
					new LoopCheckpointRewriter().Visit(node.Block) ?? node.Block));

		public override SyntaxNode? VisitForStatement(
			ForStatementSyntax node)
		{
			ForStatementSyntax visited =
				(ForStatementSyntax)(
					base.VisitForStatement(node)
						?? node);
			return visited.WithStatement(
				Guard(visited.Statement));
		}

		public override SyntaxNode? VisitForEachStatement(
			ForEachStatementSyntax node)
		{
			ForEachStatementSyntax visited =
				(ForEachStatementSyntax)(base.VisitForEachStatement(node) ?? node);
			return visited.WithStatement(Guard(visited.Statement));
		}

		public override SyntaxNode? VisitWhileStatement(
			WhileStatementSyntax node)
		{
			WhileStatementSyntax visited =
				(WhileStatementSyntax)(
					base.VisitWhileStatement(node)
						?? node);
			return visited.WithStatement(
				Guard(visited.Statement));
		}

		public override SyntaxNode? VisitDoStatement(
			DoStatementSyntax node)
		{
			DoStatementSyntax visited =
				(DoStatementSyntax)(
					base.VisitDoStatement(node)
						?? node);
			return visited.WithStatement(
				Guard(visited.Statement));
		}

		private StatementSyntax Guard(
			StatementSyntax body)
		{
			StatementSyntax checkpoint = SyntaxFactory.ParseStatement(
				cooperative
					? "if (ShouldCooperate()) yield return CpuCheckpoint();"
					: "Checkpoint();");

			if (body is BlockSyntax block)
			{
				return block.WithStatements(
					block.Statements.Insert(
						0,
						checkpoint));
			}

			return SyntaxFactory.Block(
					checkpoint,
					body.WithoutLeadingTrivia())
				.WithTriviaFrom(body);
		}
	}

	private sealed class CompiledSequenceEntryFactory : ISequenceEntrySourceFactory
	{
		private readonly Type _programType;

		public CompiledSequenceEntryFactory(Type programType)
		{
			_programType = programType;
		}

		public ISequenceEntryProvider Create(SequencingContext context)
		{
			ArgumentNullException.ThrowIfNull(context);
			return Activator.CreateInstance(_programType, context)
				as SequenceScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the Sequence lookup script.");
		}
	}

	private sealed class CompiledIncrementalPatternGenerator
		: IIncrementalRawPatternNoteGenerator
	{
		private readonly Type _programType;
		private readonly int _rowCount;
		private readonly int _channelCount;

		public CompiledIncrementalPatternGenerator(
			Type programType, int rowCount, int channelCount)
		{
			_programType = programType;
			_rowCount = rowCount;
			_channelCount = channelCount;
		}

		public IEnumerable<RawPatternStep> EnumerateRawSteps(
			SequencingContext context)
		{
			ArgumentNullException.ThrowIfNull(context);
			return Enumerate(context);
		}

		private IEnumerable<RawPatternStep> Enumerate(
			SequencingContext context)
		{
			IncrementalPatternEventReceiver receiver = new();
			PatternScriptProgram program =
				Activator.CreateInstance(
					_programType,
					context,
					receiver,
					(double)_rowCount,
					_channelCount)
					as PatternScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the incremental pattern script.");

			double lastRow = 0;
			foreach (RawPatternStep step in program.Enumerate())
			{
				if (step is RawPatternStep.Emit emission)
				{
					// A script may accidentally emit an already-passed
					// musical position. It is too late to schedule that
					// event, so discard it without rewinding or reordering.
					if (emission.Row < lastRow)
					{
						context.Diagnostics.ReportDroppedOutOfOrderNote(
							emission.Row, lastRow);
						continue;
					}
					lastRow = emission.Row;
				}
				else if (step is RawPatternStep.Advance advance
					&& advance.Row > lastRow)
				{
					lastRow = advance.Row;
				}
				yield return step;
			}
			yield return new RawPatternStep.Advance(_rowCount);
		}
	}

	private sealed class CompiledPatternGenerator
		: IRawPatternNoteGenerator
	{
		private readonly Type _programType;
		private readonly int _rowCount;
		private readonly int _channelCount;

		public CompiledPatternGenerator(
			Type programType,
			int rowCount,
			int channelCount)
		{
			_programType = programType;
			_rowCount = rowCount;
			_channelCount = channelCount;
		}

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			ArgumentNullException.ThrowIfNull(context);
			ArgumentNullException.ThrowIfNull(output);

			rowCount = _rowCount;

			PatternScriptProgram program =
				Activator.CreateInstance(
					_programType,
					context,
					new ChronologicalRawPatternNoteReceiver(output, context.Diagnostics),
					(double)_rowCount,
					_channelCount)
					as PatternScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the compiled pattern script.");
			program.Execute();
		}
	}

	private sealed class CompiledSequenceSequencer : IPlaybackPositionSequencer
	{
		private readonly ISequenceEntrySourceFactory _factory;
		private readonly ISequencePatternResolver _resolver;
		private readonly int _startOrder;
		private readonly int? _startRow;
		private readonly Func<SequenceOrderJumpEncounter, bool>?
			_shouldFollowOrderJump;
		private readonly List<CompiledPatternPlaybackPosition>
			_playbackPositions = [];

		public IReadOnlyList<CompiledPatternPlaybackPosition>
			PlaybackPositions => _playbackPositions;

		public CompiledSequenceSequencer(
			Type programType,
			ISequencePatternResolver resolver,
			int startOrder,
			int? startRow,
			Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump)
		{
			_factory = new CompiledSequenceEntryFactory(programType);
			_resolver = resolver;
			_startOrder = startOrder;
			_startRow = startRow;
			_shouldFollowOrderJump = shouldFollowOrderJump;
		}

		public void GenerateNotes(
			SequencingContext context,
			INoteReceiver output,
			out TimeSpan duration)
		{
			ArgumentNullException.ThrowIfNull(context);
			ArgumentNullException.ThrowIfNull(output);
			_playbackPositions.Clear();
			SequenceNoteProcessor.GenerateNotes(
				_factory.Create(context),
				_resolver, context, output, _startOrder, _startRow,
				out duration,
				(order, patternId, patternRow, offset) =>
					_playbackPositions.Add(
						new CompiledPatternPlaybackPosition(
							offset, patternId, patternRow, order)),
				_shouldFollowOrderJump);
		}
	}
}
