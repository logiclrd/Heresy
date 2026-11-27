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
using Microsoft.CodeAnalysis.Operations;
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

	// C# refuses compound assignment on a using local even for a void
	// instance operator. For exact NoteGroup transfers only, retain the
	// original object in a compiler-owned disposal alias and keep the
	// original variable available to the instance operator. Bind the
	// original wrapper first: no other write/ref/readonly errors are waived.
	private sealed record PatternUsingLowering(string Source,
		int[]? OriginalBoundaries, IReadOnlySet<string>? ScopeNames,
		IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
	{
		public ScriptSourceSpan Original(ScriptSourceSpan span)
		{
			if (OriginalBoundaries is not { } map) return span;
			int start = map[Math.Clamp(span.Start, 0, map.Length - 1)];
			int end = map[Math.Clamp(span.End, 0, map.Length - 1)];
			return new(start, Math.Max(0, end - start));
		}

		public IReadOnlyList<ScriptAnalysisDiagnostic> Project(
			IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
			=> OriginalBoundaries is null ? diagnostics
				: diagnostics.Select(d => d with { Span = Original(d.Span) }).ToArray();
	}

	private static PatternUsingLowering LowerGroupUsingScopes(string source)
	{
		PatternUsingLowering unchanged = new(source, null, null, []);
		if (!source.Contains("using", StringComparison.Ordinal)
			|| !source.Contains("+=", StringComparison.Ordinal))
			return unchanged;
		// The original syntax and wrapper boundary remain mandatory.
		if (HasErrors(ValidateRestrictedSyntax(source, permitManagedData: true)))
			return unchanged;
		SyntaxTree tree = CSharpSyntaxTree.ParseText(
			BuildPatternWrapper("__HeresyGroupUsingBinding", source), WrapperParseOptions);
		if (ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Pattern).Count != 0)
			return unchanged;
		CSharpCompilation compilation = CSharpCompilation.Create(
			"Heresy.GroupUsingBinding." + Guid.NewGuid().ToString("N"), [tree],
			MetadataReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
				allowUnsafe: false, checkOverflow: true));
		SemanticModel model = compilation.GetSemanticModel(tree);
		var groupType = compilation.GetTypeByMetadataName(typeof(NoteGroup).FullName!);
		var noteType = compilation.GetTypeByMetadataName(typeof(VirtualNote).FullName!);
		var root = tree.GetRoot();
		List<TextSpan> approvedLefts = [];
		Dictionary<ScriptSourceSpan, VariableDeclarationSyntax> declarations = [];
		foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
		{
			if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
				|| assignment.Parent is not ExpressionStatementSyntax
				|| assignment.Left is not IdentifierNameSyntax left
				|| !IsScriptLocation(assignment.GetLocation()))
				continue;
			// A readonly using assignment is an erroneous bound
			// expression. GetSymbolInfo on its left operand need not
			// expose the successfully looked-up local as Symbol.
			SymbolInfo leftInfo = model.GetSymbolInfo(left);
			ILocalSymbol? local = leftInfo.Symbol as ILocalSymbol
				?? leftInfo.CandidateSymbols.OfType<ILocalSymbol>().FirstOrDefault()
				?? model.LookupSymbols(left.SpanStart, name: left.Identifier.ValueText)
					.OfType<ILocalSymbol>().FirstOrDefault();
			if (local is null
				|| !SymbolEqualityComparer.Default.Equals(local.Type, groupType)
				|| noteType is null
				|| !model.ClassifyConversion(assignment.Right, noteType).IsImplicit
				|| local.DeclaringSyntaxReferences.Length != 1
				|| local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax variable
				|| variable.Parent is not VariableDeclarationSyntax declaration
				|| declaration.Variables.Count != 1)
				continue;
			if (declaration.Parent is not UsingStatementSyntax
				&& declaration.Parent is not LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
				continue;
			approvedLefts.Add(assignment.Left.Span);
			declarations.TryAdd(MapSpan(declaration.GetLocation(), source), declaration);
		}
		if (declarations.Count == 0) return unchanged;

		// Ignore exactly CS1656 on the approved left operand. Reassigning a
		// using variable, passing it by ref/out, or any other error keeps
		// its original diagnostic. The lowered canonical pass still binds
		// and authorizes the real compound operator and all other members.
		var errors = compilation.GetDiagnostics().Where(d =>
			d.Severity == DiagnosticSeverity.Error
			&& !(d.Id == "CS1656" && d.Location.SourceTree == tree
				&& approvedLefts.Any(span => span.Contains(d.Location.SourceSpan.Start))))
			.Select(d => ProjectDiagnostic(d, source)).ToArray();
		if (errors.Length != 0) return unchanged with { Diagnostics = errors };

		string prefix = "__heresyUsingScope";
		var identifiers = root.DescendantTokens()
			.Where(t => t.IsKind(SyntaxKind.IdentifierToken))
			.Select(t => t.ValueText).ToArray();
		while (identifiers.Any(id => id.StartsWith(prefix, StringComparison.Ordinal)))
			prefix += "_";
		HashSet<string> names = [];
		List<(int Start, int Length, string Replacement)> edits = [];
		void Replace(SyntaxToken token, string replacement)
		{
			var span = MapSpan(token.GetLocation(), source);
			edits.Add((span.Start, span.Length, replacement));
		}
		foreach (VariableDeclarationSyntax declaration in declarations.Values
			.OrderBy(d => d.SpanStart))
		{
			string alias = prefix + names.Count;
			names.Add(alias);
			string identifier = declaration.Variables[0].Identifier.Text;
			string capture = $" using var {alias} = {identifier}; ";
			if (declaration.Parent is LocalDeclarationStatementSyntax local)
			{
				Replace(local.UsingKeyword, new string(' ', local.UsingKeyword.Span.Length));
				edits.Add((MapSpan(local.SemicolonToken.GetLocation(), source).End, 0, capture));
			}
			else if (declaration.Parent is UsingStatementSyntax statement)
			{
				// Keep the original body (and its nested lexical scope)
				// inside a new outer block containing the captured alias.
				Replace(statement.UsingKeyword, "{");
				Replace(statement.OpenParenToken, " ");
				Replace(statement.CloseParenToken, ";" + capture);
				edits.Add((MapSpan(statement.GetLocation(), source).End, 0, " }"));
			}
		}

		// Boundary map is needed only for the exceptional lowering. It
		// preserves exact authored F6 spans and diagnostics even when a
		// declaration and subsequent calls share a line or CRLF source.
		var builder = new System.Text.StringBuilder(source.Length);
		List<int> boundaries = [0];
		int cursor = 0;
		void CopyTo(int end)
		{
			while (cursor < end)
			{
				builder.Append(source[cursor++]);
				boundaries.Add(cursor);
			}
		}
		foreach (var edit in edits.OrderBy(e => e.Start).ThenBy(e => e.Length))
		{
			CopyTo(edit.Start);
			for (int i = 0; i < edit.Replacement.Length; i++)
			{
				builder.Append(edit.Replacement[i]);
				boundaries.Add(edit.Start);
			}
			cursor = edit.Start + edit.Length;
			boundaries[^1] = cursor;
		}
		CopyTo(source.Length);
		return new(builder.ToString(), boundaries.ToArray(), names, []);
	}

	public static IReadOnlyList<ScriptAnalysisDiagnostic> AnalyzePatternSource(
		string source)
	{
		ArgumentNullException.ThrowIfNull(source);

		ScriptPatternOrderAnalysis order = ScriptPatternOrderDirective.Analyze(source);
		if (HasErrors(order.Diagnostics))
			return order.Diagnostics;
		PatternUsingLowering lowering = LowerGroupUsingScopes(order.CompilerSource);
		if (HasErrors(lowering.Diagnostics)) return lowering.Diagnostics;
		source = lowering.Source;

		const string className = "__HeresyPattern_Analysis";
		return lowering.Project(PrepareCompilation(
			source,
			BuildPatternWrapper(
				className,
				InstrumentLoops(source)),
			wrapperKind: ScriptWrapperKind.Pattern,
			patternOrder: order.Order,
			generatedUsingScopes: lowering.ScopeNames)
			.Diagnostics);
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

		ScriptPatternOrderAnalysis order = ScriptPatternOrderDirective.Analyze(definition.Source);
		if (HasErrors(order.Diagnostics))
			return new(null, order.Diagnostics);
		PatternUsingLowering lowering = LowerGroupUsingScopes(order.CompilerSource);
		if (HasErrors(lowering.Diagnostics)) return new(null, lowering.Diagnostics);
		string source = lowering.Source;

		string className =
			"__HeresyPattern_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled =
			CompileType(
				source,
				className,
				BuildPatternWrapper(
					className,
					InstrumentLoops(source)),
				typeof(PatternScriptProgram), patternOrder: order.Order,
				generatedUsingScopes: lowering.ScopeNames);

		if (!compiled.Success || compiled.Program is null)
		{
			return new ScriptCompilationResult<IRawPatternNoteGenerator>(
				null,
				lowering.Project(compiled.Diagnostics));
		}

		IRawPatternNoteGenerator generator = order.Order == ScriptPatternOrder.Relaxed
			? new CompiledRelaxedPatternGenerator(compiled.Program,
				definition.RowCount, definition.ChannelCount)
			: new CompiledPatternGenerator(compiled.Program,
				definition.RowCount, definition.ChannelCount);
		return new(generator, lowering.Project(compiled.Diagnostics));
	}

	/// <summary>
	/// Compile the invocation-local raw Pattern producer for the scheduler.
	/// Strict mode streams and drops backward raw positions; relaxed mode
	/// completes synchronous collection before replaying sorted raw events.
	/// </summary>
	public static ScriptCompilationResult<IIncrementalRawPatternNoteGenerator>
		CompileIncrementalPattern(ScriptPatternDefinition definition,
		bool instrumentActionCallsites = false)
	{
		ArgumentNullException.ThrowIfNull(definition);

		ScriptPatternOrderAnalysis order = ScriptPatternOrderDirective.Analyze(definition.Source);
		if (HasErrors(order.Diagnostics))
			return new(null, order.Diagnostics);
		PatternUsingLowering lowering = LowerGroupUsingScopes(order.CompilerSource);
		if (HasErrors(lowering.Diagnostics)) return new(null, lowering.Diagnostics);
		string source = lowering.Source;

		// Establish exact helper symbols on the unrewritten source before
		// inserting either trusted F6 callsites or streaming yields. A local
		// delegate called Note/Tempo is not a musical event generator.
		IReadOnlyList<ScriptAnalysisDiagnostic> authored =
			AnalyzePatternSource(definition.Source);
		if (HasErrors(authored))
			return new(null, authored);
		HashSet<ScriptSourceSpan> transferEmitterSpans = [];
		HashSet<ScriptSourceSpan> emitterSpans = [];
		ResolveAuthoredCallStarts(source, ScriptWrapperKind.Pattern,
			transferEmitterSpans, emitterSpans);

		if (order.Order == ScriptPatternOrder.Relaxed)
		{
			string relaxedClass = "__HeresyRelaxedPattern_" + Guid.NewGuid().ToString("N");
			// Only F6 builds the delegate provenance adapters and scopes.
			string instrumented = instrumentActionCallsites
				? InstrumentRelaxedPatternCallsites(source, emitterSpans, lowering)
				: InstrumentLoops(source);
			var relaxed = CompileType(source, relaxedClass,
				BuildPatternWrapper(relaxedClass, instrumented),
				typeof(PatternScriptProgram),
				allowInjectedPlayCallsites: instrumentActionCallsites,
				patternOrder: order.Order, generatedUsingScopes: lowering.ScopeNames);
			if (!relaxed.Success || relaxed.Program is null)
				return new(null, lowering.Project(relaxed.Diagnostics));
			return new(new CompiledRelaxedPatternGenerator(relaxed.Program,
				definition.RowCount, definition.ChannelCount), lowering.Project(relaxed.Diagnostics));
		}

		IReadOnlyList<ScriptAnalysisDiagnostic> restrictions =
			ValidateIncrementalPatternStatements(source, emitterSpans, transferEmitterSpans);
		if (restrictions.Count != 0)
			return new(null, lowering.Project(restrictions));

		string className =
			"__HeresyIncrementalPattern_" + Guid.NewGuid().ToString("N");
		ScriptCompilationResult<Type> compiled = CompileType(
			source,
			className,
			BuildIncrementalPatternWrapper(
				className,
				InstrumentIncrementalPattern(source,
					emitterSpans, instrumentActionCallsites, lowering)),
			typeof(PatternScriptProgram),
			allowInjectedPlayCallsites: instrumentActionCallsites,
			incrementalPattern: true, generatedUsingScopes: lowering.ScopeNames);

		if (!compiled.Success || compiled.Program is null)
			return new(null, lowering.Project(compiled.Diagnostics));

		return new(
			new CompiledIncrementalPatternGenerator(
				compiled.Program,
				definition.RowCount,
				definition.ChannelCount),
			lowering.Project(compiled.Diagnostics));
	}

	/// <summary>
	/// Compile an isolated raw-generation diagnostic. This factory is not a
	/// playback generator; no diagnostic events can be submitted to a timeline.
	/// Canonical authorization is identical to ordinary Pattern compilation.
	/// </summary>
	public static ScriptCompilationResult<CompiledPatternGenerationTrace>
		CompilePatternGenerationTrace(ScriptPatternDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ScriptPatternOrderAnalysis order = ScriptPatternOrderDirective.Analyze(definition.Source);
		if (HasErrors(order.Diagnostics)) return new(null, order.Diagnostics);
		PatternUsingLowering lowering = LowerGroupUsingScopes(order.CompilerSource);
		if (HasErrors(lowering.Diagnostics)) return new(null, lowering.Diagnostics);
		string source = lowering.Source;
		var authored = AnalyzePatternSource(definition.Source);
		if (HasErrors(authored)) return new(null, authored);
		HashSet<ScriptSourceSpan> transfers = [];
		HashSet<ScriptSourceSpan> emitters = [];
		var diagnosticCalls = new Dictionary<ScriptSourceSpan, IMethodSymbol>();
		var diagnosticTransfers = new HashSet<ScriptSourceSpan>();
		ResolveAuthoredCallStarts(source, ScriptWrapperKind.Pattern, transfers, emitters,
			diagnosticCalls, diagnosticTransfers);
		bool relaxed = order.Order == ScriptPatternOrder.Relaxed;
		if (!relaxed)
		{
			var restrictions = ValidateIncrementalPatternStatements(source, emitters, transfers);
			if (restrictions.Count != 0) return new(null, lowering.Project(restrictions));
		}
		string className = "__HeresyDiagnosticPattern_" + Guid.NewGuid().ToString("N");
		var adapters = new DiagnosticCallAdapters(source, lowering, relaxed, diagnosticCalls, diagnosticTransfers);
		string rewritten = relaxed
			? InstrumentRelaxedPatternCallsites(source, emitters, lowering, diagnostic: true, adapters: adapters)
			: InstrumentIncrementalPattern(source, emitters, true, lowering, diagnostic: true, adapters: adapters);
		string wrapper = relaxed ? BuildPatternWrapper(className, rewritten, diagnostic: true)
			: BuildIncrementalPatternWrapper(className, rewritten, diagnostic: true);
		var compiled = CompileType(source, className, wrapper,
			typeof(PatternScriptProgram), allowInjectedPlayCallsites: true,
			incrementalPattern: !relaxed, patternOrder: order.Order,
			generatedUsingScopes: lowering.ScopeNames);
		if (!compiled.Success || compiled.Program is null)
			return new(null, lowering.Project(compiled.Diagnostics));
		if (!typeof(DiagnosticPatternScriptProgram).IsAssignableFrom(compiled.Program))
			throw new InvalidOperationException("Diagnostic wrapper did not select its diagnostic base.");
		return new(new CompiledPatternGenerationTrace(compiled.Program, definition.Id,
			definition.Source, definition.RowCount, definition.ChannelCount, relaxed),
			lowering.Project(compiled.Diagnostics));
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
		bool incrementalPattern = false,
		ScriptPatternOrder patternOrder = ScriptPatternOrder.Strict,
		IReadOnlySet<string>? generatedUsingScopes = null)
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
				wrapperKind, patternOrder, generatedUsingScopes);
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
		ScriptWrapperKind wrapperKind = ScriptWrapperKind.None,
		ScriptPatternOrder patternOrder = ScriptPatternOrder.Strict,
		IReadOnlySet<string>? generatedUsingScopes = null)
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
				source, wrapperKind, patternOrder, generatedUsingScopes));
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
			string source, ScriptWrapperKind wrapperKind,
			ScriptPatternOrder patternOrder = ScriptPatternOrder.Strict,
			IReadOnlySet<string>? generatedUsingScopes = null)
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
				allowInjectedPlayCallsites: false)
				|| (canonicalKind == ScriptWrapperKind.Pattern
					&& SymbolEqualityComparer.Default.Equals(method.ContainingType,
						canonical.GetTypeByMetadataName(typeof(VirtualNote).FullName!))
					&& method.MethodKind == MethodKind.Ordinary
					&& (method.Name == "Dispose" || ScriptPatternEmissionPolicy.IsVirtualNoteEmitterName(method.Name))
					&& (method.Parameters.Length == 0 || method.Parameters[0].Type.Name != "SequenceCallsite"))
				|| (canonicalKind == ScriptWrapperKind.Pattern
					&& (ScriptPatternEmissionPolicy.IsNoteGroupTransfer(method, canonical)
						|| SymbolEqualityComparer.Default.Equals(method.ContainingType,
							canonical.GetTypeByMetadataName(typeof(NoteGroup).FullName!))
							&& method.MethodKind == MethodKind.Ordinary
							&& (method.Name == "Dispose" || ScriptPatternEmissionPolicy.IsNoteGroupEmitterName(method.Name))
							&& (method.Parameters.Length == 0 || method.Parameters[0].Type.Name != "SequenceCallsite"))),
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
					IsApprovedHeresyMember, IsOpaqueHeresyType,
					allowRelaxedPatternEmission: canonicalKind == ScriptWrapperKind.Pattern
						&& patternOrder == ScriptPatternOrder.Relaxed))
			{
				Location location = Location.Create(tree, finding.Span);
				AddRestrictedDiagnostic(diagnostics,
					IsScriptLocation(location)
						? MapSpan(location, source)
						: new ScriptSourceSpan(0, 0),
					finding.Reason);
			}
		}

		if (canonicalKind == ScriptWrapperKind.Pattern && !HasErrors(diagnostics))
			WarnUnprovenVirtualOwnership(entry.Body, model, canonical, source, diagnostics, generatedUsingScopes);

		return diagnostics;
	}

	private static void WarnUnprovenVirtualOwnership(BlockSyntax body,
		SemanticModel model, CSharpCompilation compilation, string source,
		List<ScriptAnalysisDiagnostic> diagnostics, IReadOnlySet<string>? generatedUsingScopes)
	{
		var handleType = compilation.GetTypeByMetadataName(typeof(VirtualNote).FullName!);
		var groupType = compilation.GetTypeByMetadataName(typeof(NoteGroup).FullName!);
		var patternType = compilation.GetTypeByMetadataName(typeof(PatternScriptProgram).FullName!);

		bool IsUsing(VariableDeclarationSyntax declaration)
		{
			if (declaration.Parent is UsingStatementSyntax
				|| declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
				return true;
			if (generatedUsingScopes is null
				|| declaration.Parent is not LocalDeclarationStatementSyntax local
				|| declaration.Variables.Count != 1
				|| model.GetDeclaredSymbol(declaration.Variables[0]) is not ILocalSymbol owned)
				return false;
			SyntaxList<StatementSyntax> statements = local.Parent switch
			{
				BlockSyntax block => block.Statements,
				SwitchSectionSyntax section => section.Statements,
				_ => default,
			};
			int next = statements.IndexOf(local) + 1;
			return next > 0 && next < statements.Count
				&& statements[next] is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } scope
				&& scope.Declaration.Variables.Count == 1
				&& scope.Declaration.Variables[0] is { } alias
				&& generatedUsingScopes.Contains(alias.Identifier.ValueText)
				&& alias.Initializer?.Value is { } captured
				&& SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(captured).Symbol, owned);
		}

		bool IsGroupTransfer(AssignmentExpressionSyntax transfer)
			=> transfer.Parent is ExpressionStatementSyntax
				&& model.GetOperation(transfer) is ICompoundAssignmentOperation
					{ OperatorMethod: { } op }
				&& ScriptPatternEmissionPolicy.IsNoteGroupTransfer(op, compilation);

		bool HasDirectGroupDisposal(VariableDeclarationSyntax declaration,
			ILocalSymbol destination)
		{
			if (IsUsing(declaration)) return true;
			if (declaration.Parent is not LocalDeclarationStatementSyntax local
				|| local.Parent is not BlockSyntax block)
				return false;
			foreach (StatementSyntax statement in block.Statements.Skip(block.Statements.IndexOf(local) + 1))
			{
				if (statement is not ExpressionStatementSyntax expression)
					return false;
				if (expression.Expression is AssignmentExpressionSyntax transfer
					&& IsGroupTransfer(transfer))
					continue;
				if (expression.Expression is not InvocationExpressionSyntax call)
					return false;
				if (call.Expression is MemberAccessExpressionSyntax access
					&& SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, destination)
					&& model.GetSymbolInfo(call).Symbol is IMethodSymbol method
					&& method.Name == "Dispose"
					&& SymbolEqualityComparer.Default.Equals(method.ContainingType, groupType))
					return true;
				if (call.DescendantNodes().Any(n => n is AssignmentExpressionSyntax
					or AnonymousFunctionExpressionSyntax
					or ArgumentSyntax { RefKindKeyword.RawKind: not 0 }))
					return false;
			}
			return false;
		}

		bool IsOwningGroupTransfer(AssignmentExpressionSyntax transfer)
		{
			if (!IsGroupTransfer(transfer)
				|| model.GetSymbolInfo(transfer.Left).Symbol is not ILocalSymbol destination)
				return false;
			// Prove the ordinary lexical using-group or simple explicit
			// disposal case. Complex flow and group aliases stay conservative.
			return destination.DeclaringSyntaxReferences.Any(reference =>
				reference.GetSyntax() is VariableDeclaratorSyntax
				{
					Parent: VariableDeclarationSyntax declaration,
					Initializer.Value: InvocationExpressionSyntax creation,
				}
				&& model.GetSymbolInfo(creation).Symbol is IMethodSymbol factory
				&& factory.Name == "NewNoteGroup"
				&& SymbolEqualityComparer.Default.Equals(factory.ContainingType, patternType)
				&& HasDirectGroupDisposal(declaration, destination));
		}

		foreach (InvocationExpressionSyntax call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
				|| !SymbolEqualityComparer.Default.Equals(method.ContainingType, patternType))
				continue;
			bool handle = method.Name == "NoteVirtual"
				&& SymbolEqualityComparer.Default.Equals(method.ReturnType, handleType);
			bool group = method.Name == "NewNoteGroup"
				&& SymbolEqualityComparer.Default.Equals(method.ReturnType, groupType);
			if (!handle && !group) continue;
			if (handle && call.Parent is AssignmentExpressionSyntax directTransfer
				&& ReferenceEquals(directTransfer.Right, call)
				&& IsOwningGroupTransfer(directTransfer))
				continue;
			if (call.Parent is EqualsValueClauseSyntax initializer
				&& initializer.Parent is VariableDeclaratorSyntax variable
				&& variable.Parent is VariableDeclarationSyntax declaration)
			{
				if (IsUsing(declaration)) continue;
				if (group && model.GetDeclaredSymbol(variable) is ILocalSymbol groupLocal
					&& HasDirectGroupDisposal(declaration, groupLocal))
					continue;
				if (declaration.Parent is LocalDeclarationStatementSyntax local
					&& local.Parent is BlockSyntax block
					&& model.GetDeclaredSymbol(variable) is ILocalSymbol owned)
				{
					bool disposed = false;
					foreach (StatementSyntax next in block.Statements.Skip(block.Statements.IndexOf(local) + 1))
					{
						if (next is not ExpressionStatementSyntax expression)
							break;
						if (expression.Expression is AssignmentExpressionSyntax transfer
							&& IsOwningGroupTransfer(transfer))
						{
							if (handle && SymbolEqualityComparer.Default.Equals(
								model.GetSymbolInfo(transfer.Right).Symbol, owned))
							{
								disposed = true;
								break;
							}
							continue;
						}
						if (expression.Expression is not InvocationExpressionSyntax action)
							break;
						if (action.Expression is MemberAccessExpressionSyntax access
							&& SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, owned)
							&& model.GetSymbolInfo(action).Symbol is IMethodSymbol target
							&& target.Name == "Dispose"
							&& SymbolEqualityComparer.Default.Equals(target.ContainingType, method.ReturnType))
						{
							disposed = true;
							break;
						}
						if (action.DescendantNodes().Any(n => n is AssignmentExpressionSyntax
							or AnonymousFunctionExpressionSyntax
							or ArgumentSyntax { RefKindKeyword.RawKind: not 0 }))
							break;
					}
					if (disposed) continue;
				}
			}
			diagnostics.Add(new ScriptAnalysisDiagnostic("HRS2101", ScriptDiagnosticSeverity.Warning,
				handle
					? "VirtualNote ownership termination is not proven; use a using scope, Dispose or transfer to an owning group."
					: "NoteGroup ownership termination is not proven; use a using scope or explicitly Dispose the group.",
				MapSpan(call.GetLocation(), source)));
		}
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateRestrictedSyntax(string source, bool permitManagedData = false)
	{
		SyntaxNode root = permitManagedData
			? ParseAuthoredBody(source)
			: CSharpSyntaxTree.ParseText(source, ScriptParseOptions).GetRoot();
		int sourceOffset = permitManagedData ? AuthoredBodyOffset : 0;
		List<ScriptAnalysisDiagnostic> diagnostics = [];

		foreach (SyntaxTrivia trivia
			in root.DescendantTrivia(descendIntoTrivia: true))
		{
			if (trivia.IsDirective)
			{
				AddRestrictedDiagnostic(
					diagnostics,
					new ScriptSourceSpan(Math.Max(0, trivia.SpanStart - sourceOffset), trivia.Span.Length),
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
					new ScriptSourceSpan(Math.Max(0, node.SpanStart - sourceOffset), node.Span.Length),
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
			&& (method.Name == "Play" || ScriptPatternEmissionPolicy.IsEmitterName(method.Name))
			&& !allowInjectedPlayCallsites)
			return false;

		INamedTypeSymbol containingType = method.ContainingType;
		if (SymbolEqualityComparer.Default.Equals(containingType, patternBase))
			return method.MethodKind == MethodKind.Ordinary
				&& (method.Name is "_O" or "Random" or "Checkpoint"
					|| method.Name == "NewNoteGroup"
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

	// Parse in the actual method-body context, including top-level using
	// declarations. Newlines keep any authored directive on its own line.
	private const int AuthoredBodyOffset = 2;
	private static BlockSyntax ParseAuthoredBody(string source)
		=> (BlockSyntax)SyntaxFactory.ParseStatement("{\n" + source + "\n}", options: WrapperParseOptions);
	private static string AuthoredBodyText(BlockSyntax body)
	{
		string text = body.ToFullString();
		// Rewriters can move the synthetic trailing newline onto an
		// inner statement or replace its trivia altogether. Slice by
		// brace token positions, never assume two trailing characters.
		int first = body.OpenBraceToken.Span.End - body.FullSpan.Start;
		int last = body.CloseBraceToken.SpanStart - body.FullSpan.Start;
		string contents = text.Substring(first, last - first);
		return contents.StartsWith("\n", StringComparison.Ordinal)
			? contents.Substring(1) : contents;
	}

	private static string InstrumentLoops(string source)
	{
		BlockSyntax body = ParseAuthoredBody(source);
		return AuthoredBodyText((BlockSyntax)new LoopCheckpointRewriter().Visit(body)!);
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
		string source, ScriptWrapperKind kind, HashSet<ScriptSourceSpan>? transferEmitterSpans = null,
		HashSet<ScriptSourceSpan>? emitterSpans = null,
		Dictionary<ScriptSourceSpan, IMethodSymbol>? diagnosticCalls = null,
		HashSet<ScriptSourceSpan>? diagnosticTransfers = null)
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
			if (diagnosticCalls is not null && (trusted
				|| method.Name == "NewNoteGroup" && method.Parameters.Length == 0
				&& SymbolEqualityComparer.Default.Equals(method.ContainingType,
					compilation.GetTypeByMetadataName(typeof(PatternScriptProgram).FullName!))))
				diagnosticCalls.Add(MapSpan(invocation.GetLocation(), source), method);
			if (trusted)
			{
				ScriptSourceSpan span = MapSpan(invocation.GetLocation(), source);
				positions.Add(span.Start);
				emitterSpans?.Add(span);
				if (method.Name == "NoteVirtual"
					&& invocation.Parent is AssignmentExpressionSyntax assignment
					&& assignment.IsKind(SyntaxKind.AddAssignmentExpression)
					&& ReferenceEquals(assignment.Right, invocation)
					&& assignment.Parent is ExpressionStatementSyntax
					&& semantic.GetOperation(assignment) is ICompoundAssignmentOperation
						{ OperatorMethod: { } transfer }
					&& ScriptPatternEmissionPolicy.IsNoteGroupTransfer(transfer, compilation))
					transferEmitterSpans?.Add(span);
			}
		}
		if (diagnosticTransfers is not null)
			foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
				if (IsScriptLocation(assignment.GetLocation())
					&& semantic.GetOperation(assignment) is ICompoundAssignmentOperation { OperatorMethod: { } transfer }
					&& ScriptPatternEmissionPolicy.IsNoteGroupTransfer(transfer, compilation))
					diagnosticTransfers.Add(MapSpan(assignment.GetLocation(), source));
		return positions;
	}

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateIncrementalPatternStatements(
			string source, IReadOnlySet<ScriptSourceSpan> emitterSpans, IReadOnlySet<ScriptSourceSpan> transferEmitterSpans)
	{
		SyntaxNode root = ParseAuthoredBody(source);
		List<ScriptAnalysisDiagnostic> diagnostics = [];
		foreach (InvocationExpressionSyntax invocation
			in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			var span = new ScriptSourceSpan(invocation.SpanStart - AuthoredBodyOffset, invocation.Span.Length);
			if (!emitterSpans.Contains(span))
				continue;
			if (invocation.Parent is ExpressionStatementSyntax statement
				&& ReferenceEquals(statement.Expression, invocation))
				continue;
			if (transferEmitterSpans.Contains(span))
				continue;
			if (invocation.Parent is EqualsValueClauseSyntax initializer
				&& initializer.Parent is VariableDeclaratorSyntax variable
				&& variable.Parent is VariableDeclarationSyntax declaration
				&& declaration.Variables.Count == 1
				&& declaration.Parent is LocalDeclarationStatementSyntax or FieldDeclarationSyntax or UsingStatementSyntax)
				continue;
			diagnostics.Add(new ScriptAnalysisDiagnostic(
				RestrictedFeatureCode, ScriptDiagnosticSeverity.Error,
				"Streaming note helpers must be direct statements, a single local initializer, or a direct NoteGroup += NoteVirtual(...) transfer.",
				new ScriptSourceSpan(invocation.Span.Start - AuthoredBodyOffset, invocation.Span.Length)));
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

	private const string DiagnosticLoopAnnotation = "HeresyDiagnosticLoop";

	private static BlockSyntax AnnotateDiagnosticLoops(BlockSyntax body, PatternUsingLowering lowering)
	{
		var loops = body.DescendantNodes().OfType<StatementSyntax>()
			.Where(node => node is ForStatementSyntax or ForEachStatementSyntax
				or WhileStatementSyntax or DoStatementSyntax).ToArray();
		return body.ReplaceNodes(loops, (original, rewritten) =>
		{
			var span = lowering.Original(new ScriptSourceSpan(
				original.SpanStart - AuthoredBodyOffset, original.Span.Length));
			return rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(
				DiagnosticLoopAnnotation, TraceCallsite(span)));
		});
	}

	private static string InstrumentIncrementalPattern(string source,
		IReadOnlySet<ScriptSourceSpan> emitterSpans, bool instrumentActionCallsites,
		PatternUsingLowering lowering, bool diagnostic = false, DiagnosticCallAdapters? adapters = null)
	{
		// Parse the authored statements in their actual method-body shape,
		// rather than treating top-level locals as script fields. The
		// synthetic opening brace shifts original offsets by exactly one.
		BlockSyntax body = ParseAuthoredBody(source);
		if (diagnostic) body = AnnotateDiagnosticLoops(body, lowering);
		body = (BlockSyntax)new StrictPatternEmitterRewriter(
			emitterSpans, instrumentActionCallsites, AuthoredBodyOffset, lowering, adapters).Visit(body)!;
		body = (BlockSyntax)new LoopCheckpointRewriter(cooperative: true, diagnostic: diagnostic).Visit(body)!;
		return AuthoredBodyText(body) + (adapters is null ? "" : adapters.Declarations);
	}

	private static string InstrumentRelaxedPatternCallsites(string source,
		IReadOnlySet<ScriptSourceSpan> emitterSpans, PatternUsingLowering lowering,
		bool diagnostic = false, DiagnosticCallAdapters? adapters = null)
	{
		SyntaxNode root = ParseAuthoredBody(source);
		if (diagnostic) root = AnnotateDiagnosticLoops((BlockSyntax)root, lowering);
		string prefix = "__heresyTrace";
		var identifiers = root.DescendantTokens()
			.Where(t => t.IsKind(SyntaxKind.IdentifierToken))
			.Select(t => t.ValueText).ToArray();
		while (identifiers.Any(id => id.StartsWith(prefix, StringComparison.Ordinal)))
			prefix += "_";

		var (delegates, methodGroups) = ResolveRelaxedTraceSites(source);
		Dictionary<ScriptSourceSpan, string> replacements = [];
		Dictionary<ScriptSourceSpan, BoundTraceSite> boundReplacements = [];
		List<string> localAdapters = [];
		// A single reference site for a bound emitter has exact fallback
		// provenance. If multiple sites reference the same method, do not
		// substitute different delegate targets: that would change delegate
		// equality, combination and removal in F6. The authored invocation
		// scope still supplies exact provenance in that case.
		foreach (var (method, sites) in methodGroups)
		{
			if (sites.Count != 1)
				continue;
			MethodGroupTraceSite site = sites[0];
			ScriptSourceSpan span = site.Span;
			if (site.BoundReceiver)
			{
				if (site.DelegateType is { CanBeReferencedByName: true } delegateType)
					boundReplacements.Add(span, new(method, delegateType));
				continue;
			}
			string name = prefix + "Group" + localAdapters.Count;
			replacements.Add(span, name);
			string parameters = string.Join(", ", method.Parameters.Select(p =>
				p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
				+ " @" + p.Name + DefaultParameterSyntax(p)));
			string arguments = string.Join(", ", method.Parameters.Select(p => "@" + p.Name));
			string returnType = method.ReturnsVoid ? "void"
				: method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			string returnPrefix = method.ReturnsVoid ? "" : "return ";
			localAdapters.Add($"{returnType} {name}({parameters}) {{ {returnPrefix}base.{method.Name}({TraceCallsite(lowering.Original(span))}, {arguments}); }}");
		}

		root = new RelaxedPatternEmitterCallsiteRewriter(emitterSpans, delegates,
			replacements, boundReplacements, prefix, lowering, diagnostic, adapters).Visit(root) ?? root;
		root = new LoopCheckpointRewriter(diagnostic: diagnostic).Visit(root) ?? root;
		// Local adapter declarations are compiler-owned, after the original
		// authored source, and never included in the canonical API audit.
		return AuthoredBodyText((BlockSyntax)root) + "\n" + string.Join("\n", localAdapters)
			+ (adapters is null ? "" : adapters.Declarations);
	}

	private static string DefaultParameterSyntax(IParameterSymbol parameter)
		=> !parameter.HasExplicitDefaultValue ? ""
			: " = " + (parameter.ExplicitDefaultValue switch
			{
				null => "default",
				bool b => b ? "true" : "false",
				object value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
			});

	private static string TraceCallsite(ScriptSourceSpan span)
		=> $"new global::Heresy.Core.Sequences.SequenceCallsite({span.Start}, {span.Length})";

	private sealed record DelegateTraceSite(IMethodSymbol Method, bool ExplicitInvoke);
	private sealed record MethodGroupTraceSite(ScriptSourceSpan Span, bool BoundReceiver,
		INamedTypeSymbol? DelegateType);
	private sealed record BoundTraceSite(IMethodSymbol Method, INamedTypeSymbol DelegateType);

	private static (Dictionary<ScriptSourceSpan, DelegateTraceSite> Delegates,
		Dictionary<IMethodSymbol, List<MethodGroupTraceSite>> MethodGroups)
		ResolveRelaxedTraceSites(string source)
	{
		SyntaxTree tree = CSharpSyntaxTree.ParseText(
			BuildPatternWrapper("__HeresyRelaxedTraceResolution", source), WrapperParseOptions);
		CSharpCompilation compilation = CSharpCompilation.Create(
			"Heresy.RelaxedTraceResolution." + Guid.NewGuid().ToString("N"),
			[tree], MetadataReferences, new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false, checkOverflow: true));
		SemanticModel semantic = compilation.GetSemanticModel(tree);
		Dictionary<ScriptSourceSpan, DelegateTraceSite> delegates = [];
		Dictionary<IMethodSymbol, List<MethodGroupTraceSite>> groups =
			new(SymbolEqualityComparer.Default);
		var entry = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
			.Single(m => m.Identifier.ValueText == "ExecuteScript");
		IOperation body = semantic.GetOperation(entry.Body!)
			?? throw new InvalidOperationException("Relaxed trace source did not bind.");
		Stack<IOperation> pending = new();
		pending.Push(body);
		while (pending.TryPop(out IOperation? operation))
		{
			if (IsScriptLocation(operation.Syntax.GetLocation()))
			{
				if (operation is IInvocationOperation invoke
					&& invoke.TargetMethod.MethodKind == MethodKind.DelegateInvoke
					&& invoke.Syntax is InvocationExpressionSyntax invocation)
				{
					bool explicitInvoke = invocation.Expression is MemberAccessExpressionSyntax access
						&& semantic.GetSymbolInfo(access).Symbol is IMethodSymbol
							{ MethodKind: MethodKind.DelegateInvoke };
					delegates[MapSpan(invocation.GetLocation(), source)] =
						new(invoke.TargetMethod, explicitInvoke);
				}
				else if (operation is IMethodReferenceOperation reference
					&& reference.Parent is IDelegateCreationOperation creation
					&& reference.Syntax is IdentifierNameSyntax or MemberAccessExpressionSyntax
					&& ScriptPatternEmissionPolicy.IsEmitterMethod(reference.Method, compilation))
				{
					if (!groups.TryGetValue(reference.Method, out var sites))
						groups.Add(reference.Method, sites = []);
					var span = MapSpan(reference.Syntax.GetLocation(), source);
					if (!sites.Any(site => site.Span == span))
						sites.Add(new(span, reference.Syntax is MemberAccessExpressionSyntax,
							creation.Type as INamedTypeSymbol));
				}
			}
			foreach (IOperation child in operation.ChildOperations)
				pending.Push(child);
		}
		return (delegates, groups);
	}

	// Rewrite only original bound nodes, before any trusted code changes
	// their spans. Wrapping the receiver rather than moving the original
	// arguments into a lambda preserves evaluation order, named/optional
	// argument binding, short-circuit evaluation and by-ref arguments.
	private sealed class RelaxedPatternEmitterCallsiteRewriter(
		IReadOnlySet<ScriptSourceSpan> emitterSpans,
		IReadOnlyDictionary<ScriptSourceSpan, DelegateTraceSite> delegates,
		IReadOnlyDictionary<ScriptSourceSpan, string> methodGroups,
		IReadOnlyDictionary<ScriptSourceSpan, BoundTraceSite> boundGroups,
		string prefix, PatternUsingLowering lowering, bool diagnostic, DiagnosticCallAdapters? adapters) : CSharpSyntaxRewriter
	{
		private static ScriptSourceSpan Span(SyntaxNode node)
			=> new(node.SpanStart - AuthoredBodyOffset, node.Span.Length);

		public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
			=> methodGroups.TryGetValue(Span(node), out string? name)
				? SyntaxFactory.IdentifierName(name).WithTriviaFrom(node)
				: base.VisitIdentifierName(node);

		public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
		{
			var rewritten = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
			ScriptSourceSpan span = Span(node);
			if (!boundGroups.TryGetValue(span, out BoundTraceSite? site))
				return rewritten;
			string delegateType = site.DelegateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			string target = prefix + "BoundTarget";
			string previous = prefix + "BoundPrevious";
			string parameters = string.Join(", ", site.Method.Parameters.Select((p, i) =>
				p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " + prefix + "BoundArg" + i));
			string arguments = string.Join(", ", site.Method.Parameters.Select((p, i) => prefix + "BoundArg" + i));
			string returnPrefix = site.Method.ReturnsVoid ? "" : "return ";
			// Evaluate the real method-group conversion first, exactly once.
			// Cache the adapter against that delegate, not against the source
			// spelling or the variable that formerly held its receiver.
			string receiverText = diagnostic ? prefix + "ReceiverPlaceholder" : rewritten.WithoutTrivia().ToFullString();
			string adapter = $"base.__HeresyBindTrace(({delegateType}){receiverText}, "
				+ $"{target} => ({parameters}) => {{ "
				+ $"var {previous} = base.__HeresyPushFallbackCallsite({TraceCallsite(lowering.Original(span))}); "
				+ $"try {{ {returnPrefix}{target}({arguments}); }} "
				+ $"finally {{ base.__HeresyRestoreDelegateCallsite({previous}); }} }})";
			return ParseAdapter(adapter, rewritten).WithTriviaFrom(node);
		}

		public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
		{
			var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
			ScriptSourceSpan span = Span(node);
			if (delegates.TryGetValue(span, out DelegateTraceSite? site))
			{
				IMethodSymbol method = site.Method;
				// Explicit .Invoke is semantically the same delegate call;
				// its receiver is the expression left of .Invoke.
				ExpressionSyntax receiver = site.ExplicitInvoke
					&& rewritten.Expression is MemberAccessExpressionSyntax rewrittenAccess
					? rewrittenAccess.Expression : rewritten.Expression;
				string target = prefix + "Target";
				string previous = prefix + "Previous";
				string parameters = string.Join(", ", method.Parameters.Select((p, i) =>
					ParameterModifier(p.RefKind, declaration: true)
					+ p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
					+ " " + prefix + "Arg" + i));
				string arguments = string.Join(", ", method.Parameters.Select((p, i) =>
					ParameterModifier(p.RefKind, declaration: false) + prefix + "Arg" + i));
				string returnPrefix = method.ReturnsVoid ? ""
					: method.ReturnsByRef || method.ReturnsByRefReadonly ? "return ref " : "return ";
				string adapter =
					$"base.__HeresyWrapDelegate({(diagnostic ? prefix + "ReceiverPlaceholder" : receiver.WithoutTrivia().ToFullString())}, "
					+ $"{target} => ({parameters}) => {{ "
					+ $"var {previous} = base.__HeresyPushDelegateCallsite({TraceCallsite(lowering.Original(span))}); "
					+ $"try {{ {returnPrefix}{target}({arguments}); }} "
					+ $"finally {{ base.__HeresyRestoreDelegateCallsite({previous}); }} }})";
				return rewritten.WithExpression(ParseAdapter(adapter, receiver)
					.WithTriviaFrom(rewritten.Expression));
			}
			if (adapters?.RewriteInvocation(node, rewritten) is { } diagnosticCall)
				return diagnosticCall;
			if (!emitterSpans.Contains(span))
				return rewritten;

			ArgumentSyntax callsite = SyntaxFactory.Argument(
				SyntaxFactory.ParseExpression(TraceCallsite(lowering.Original(span))));
			return rewritten.WithArgumentList(rewritten.ArgumentList.WithArguments(
				rewritten.ArgumentList.Arguments.Insert(0, callsite)));
		}

		public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
		{
			var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
			return adapters?.RewriteTransfer(node, rewritten) ?? (ExpressionSyntax)rewritten;
		}

		private ExpressionSyntax ParseAdapter(string adapter, ExpressionSyntax receiver)
		{
			ExpressionSyntax parsed = SyntaxFactory.ParseExpression(adapter);
			if (!diagnostic) return parsed;
			var placeholder = parsed.DescendantNodes().OfType<IdentifierNameSyntax>()
				.Single(n => n.Identifier.ValueText == prefix + "ReceiverPlaceholder");
			return parsed.ReplaceNode(placeholder, receiver.WithoutTrivia());
		}

		private static string ParameterModifier(RefKind kind, bool declaration)
			=> kind switch
			{
				RefKind.Ref => "ref ",
				RefKind.Out => "out ",
				RefKind.In => "in ",
				RefKind.RefReadOnlyParameter => declaration ? "ref readonly " : "in ",
				_ => "",
			};
	}

	/// <summary>Only the diagnostic variant emits these typed adapters.
	/// Original arguments stay in their original order at the callsite; a
	/// bound receiver is evaluated first, exactly once. No reflection or
	/// alternate musical implementation is used at runtime.</summary>
	private sealed class DiagnosticCallAdapters
	{
		private readonly PatternUsingLowering _lowering;
		private readonly bool _relaxed;
		private readonly IReadOnlyDictionary<ScriptSourceSpan, IMethodSymbol> _calls;
		private readonly IReadOnlySet<ScriptSourceSpan> _transfers;
		private readonly string _prefix;
		private readonly List<string> _declarations = [];

		internal DiagnosticCallAdapters(string source, PatternUsingLowering lowering, bool relaxed,
			IReadOnlyDictionary<ScriptSourceSpan, IMethodSymbol> calls, IReadOnlySet<ScriptSourceSpan> transfers)
		{
			_lowering = lowering;
			_relaxed = relaxed;
			_calls = calls;
			_transfers = transfers;
			string prefix = "__heresyDiagnosticAdapter";
			var identifiers = ParseAuthoredBody(source).DescendantTokens()
				.Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToArray();
			while (identifiers.Any(id => id.StartsWith(prefix, StringComparison.Ordinal))) prefix += "_";
			_prefix = prefix;
		}

		internal string Declarations => "\n" + string.Join("\n", _declarations);
		private static ScriptSourceSpan Span(SyntaxNode node)
			=> new(node.SpanStart - AuthoredBodyOffset, node.Span.Length);

		internal ExpressionSyntax? RewriteInvocation(InvocationExpressionSyntax original, InvocationExpressionSyntax rewritten)
		{
			var span = Span(original);
			if (!_calls.TryGetValue(span, out var method)) return null;
			string origin = TraceCallsite(_lowering.Original(span));
			if (method.Name == "NewNoteGroup")
				return SyntaxFactory.ParseExpression("base.__HeresyTraceNewGroup(" + origin + ")").WithTriviaFrom(original);
			bool bound = method.ContainingType.Name is nameof(NoteGroup) or nameof(VirtualNote);
			bool group = method.ContainingType.Name == nameof(NoteGroup);
			string receiver = _prefix + "Receiver";
			string name = _prefix + _declarations.Count;
			string parameters = string.Join(", ", method.Parameters.Select(p =>
				p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
				+ " @" + p.Name + DefaultParameterSyntax(p)));
			if (bound) parameters = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
				+ " " + receiver + ", " + parameters;
			string arguments = string.Join(", ", method.Parameters.Select(p => "@" + p.Name));
			string returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			string call = (method.ReturnsVoid ? "" : "return ") + (bound ? receiver : "base")
				+ "." + method.Name + "(" + origin + ", " + arguments + ");";
			string scopeArgs = origin + ", \"" + method.Name + "\", " + (group ? receiver : "null");
			string body = _relaxed
				? "using var " + _prefix + "Scope = base.__HeresyEnterTraceInvocation(" + scopeArgs + "); " + call
				: "base.__HeresyBeginTraceInvocation(" + scopeArgs + "); try { " + call
					+ " } catch { base.__HeresyEndTraceInvocation(); throw; }";
			_declarations.Add(returnType + " " + name + "(" + parameters + ") { " + body + " }");
			var args = rewritten.ArgumentList.Arguments;
			if (bound)
			{
				if (rewritten.Expression is not MemberAccessExpressionSyntax access)
					throw new InvalidOperationException("Bound diagnostic emitter receiver missing.");
				args = args.Insert(0, SyntaxFactory.Argument(access.Expression));
			}
			return rewritten.WithExpression(SyntaxFactory.IdentifierName(name).WithTriviaFrom(rewritten.Expression))
				.WithArgumentList(rewritten.ArgumentList.WithArguments(args));
		}

		internal ExpressionSyntax? RewriteTransfer(AssignmentExpressionSyntax original, AssignmentExpressionSyntax rewritten)
		{
			var span = Span(original);
			if (!_transfers.Contains(span)) return null;
			return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("base.__HeresyTraceTransfer"),
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
					SyntaxFactory.Argument(SyntaxFactory.ParseExpression(TraceCallsite(_lowering.Original(span)))),
					SyntaxFactory.Argument(rewritten.Left), SyntaxFactory.Argument(rewritten.Right) })))
				.WithTriviaFrom(original);
		}
	}

	private const string TrustedFanoutDrainAnnotation = "HeresyTrustedFanoutDrain";

	private sealed class StrictPatternEmitterRewriter(
		IReadOnlySet<ScriptSourceSpan> emitterSpans, bool instrumentActionCallsites, int sourceOffset,
		PatternUsingLowering lowering, DiagnosticCallAdapters? adapters)
		: CSharpSyntaxRewriter
	{
		private bool IsEmitter(InvocationExpressionSyntax invocation)
			=> emitterSpans.Contains(new(invocation.SpanStart - sourceOffset, invocation.Span.Length));

		private StatementSyntax YieldPending()
		{
			var drain = SyntaxFactory.ParseStatement("while (base.HasPendingEvent()) yield return base.EmitPendingStep();\n")
				.WithAdditionalAnnotations(new SyntaxAnnotation(TrustedFanoutDrainAnnotation));
			return adapters is null ? drain : SyntaxFactory.Block(drain,
				SyntaxFactory.ParseStatement("base.__HeresyEndTraceInvocation();"));
		}

		private bool HasEmitterInitializer(VariableDeclarationSyntax declaration)
			=> declaration.Variables.Count == 1
				&& declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax invoke
				&& IsEmitter(invoke);

		public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
		{
			var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
			if (adapters?.RewriteInvocation(node, visited) is { } diagnosticCall)
				return diagnosticCall;
			if (!instrumentActionCallsites || !IsEmitter(node))
				return visited;
			var span = lowering.Original(new ScriptSourceSpan(node.SpanStart - sourceOffset, node.Span.Length));
			var callsite = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(TraceCallsite(span)));
			return visited.WithArgumentList(visited.ArgumentList.WithArguments(
				visited.ArgumentList.Arguments.Insert(0, callsite)));
		}

		public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
		{
			var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
			return adapters?.RewriteTransfer(node, rewritten) ?? (ExpressionSyntax)rewritten;
		}

		public override SyntaxNode? VisitBlock(BlockSyntax node)
		{
			List<StatementSyntax> statements = [];
			foreach (StatementSyntax statement in node.Statements)
			{
				statements.Add((StatementSyntax)Visit(statement)!);
				if (statement is LocalDeclarationStatementSyntax local
					&& HasEmitterInitializer(local.Declaration))
					statements.Add(YieldPending());
			}
			return node.WithStatements(SyntaxFactory.List(statements));
		}

		public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
		{
			List<StatementSyntax> statements = [];
			foreach (StatementSyntax statement in node.Statements)
			{
				statements.Add((StatementSyntax)Visit(statement)!);
				if (statement is LocalDeclarationStatementSyntax local
					&& HasEmitterInitializer(local.Declaration))
					statements.Add(YieldPending());
			}
			return node.WithStatements(SyntaxFactory.List(statements));
		}

		public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
		{
			var visited = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
			if (node.Declaration is null || !HasEmitterInitializer(node.Declaration))
				return visited;
			// Keep the yield INSIDE the using scope so disposing the
			// suspended enumerator always disposes the initialized lease.
			return visited.WithStatement(SyntaxFactory.Block(
				YieldPending(), visited.Statement));
		}

		public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
		{
			var visited = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;
			bool emits = node.Expression is InvocationExpressionSyntax invocation && IsEmitter(invocation)
				|| node.Expression is AssignmentExpressionSyntax assignment
					&& assignment.IsKind(SyntaxKind.AddAssignmentExpression)
					&& assignment.Right is InvocationExpressionSyntax start && IsEmitter(start);
			if (!emits) return visited;
			return SyntaxFactory.Block(visited.WithoutLeadingTrivia(), YieldPending())
				.WithTriviaFrom(node);
		}
	}

	private static string PatternWrapperBase(bool diagnostic)
		=> diagnostic ? "global::Heresy.Scripting.Compilation.DiagnosticPatternScriptProgram"
			: "PatternScriptProgram";

	private static string PatternWrapperExtraParameter(bool diagnostic)
		=> diagnostic
			? ", global::Heresy.Scripting.Compilation.PatternGenerationTraceBudget traceBudget"
			: "";

	private static string BuildIncrementalPatternWrapper(
		string className, string source, bool diagnostic = false)
		=> $$"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using Heresy.Core.Sequences;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class {{className}} : {{PatternWrapperBase(diagnostic)}}
			{
				public {{className}}(
					SequencingContext context,
					INoteReceiver output,
					double rowCount,
					int channelCount{{PatternWrapperExtraParameter(diagnostic)}})
					: base(context, output, rowCount, channelCount{{(diagnostic ? ", traceBudget" : "")}})
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
		string source, bool diagnostic = false)
		=> $$"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using Heresy.Core.Sequences;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;
			using static Heresy.Scripting.Runtime.ScriptPitchMath;

			public sealed class {{className}} : {{PatternWrapperBase(diagnostic)}}
			{
				public {{className}}(
					SequencingContext context,
					INoteReceiver output,
					double rowCount,
					int channelCount{{PatternWrapperExtraParameter(diagnostic)}})
					: base(context, output, rowCount, channelCount{{(diagnostic ? ", traceBudget" : "")}})
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

	private sealed class LoopCheckpointRewriter(bool cooperative = false, bool diagnostic = false)
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
					new LoopCheckpointRewriter(diagnostic: diagnostic).Visit(node.Body) ?? node.Body));

		public override SyntaxNode? VisitParenthesizedLambdaExpression(
			ParenthesizedLambdaExpressionSyntax node)
			=> !cooperative
				? base.VisitParenthesizedLambdaExpression(node)
				: node.WithBody((CSharpSyntaxNode)(
					new LoopCheckpointRewriter(diagnostic: diagnostic).Visit(node.Body) ?? node.Body));

		public override SyntaxNode? VisitAnonymousMethodExpression(
			AnonymousMethodExpressionSyntax node)
			=> !cooperative
				? base.VisitAnonymousMethodExpression(node)
				: node.WithBlock((BlockSyntax)(
					new LoopCheckpointRewriter(diagnostic: diagnostic).Visit(node.Block) ?? node.Block));

		public override SyntaxNode? VisitForStatement(
			ForStatementSyntax node)
		{
			ForStatementSyntax visited =
				(ForStatementSyntax)(
					base.VisitForStatement(node)
						?? node);
			return WrapLoop(visited.WithStatement(Guard(visited.Statement)), node);
		}

		public override SyntaxNode? VisitForEachStatement(
			ForEachStatementSyntax node)
		{
			ForEachStatementSyntax visited =
				(ForEachStatementSyntax)(base.VisitForEachStatement(node) ?? node);
			return WrapLoop(visited.WithStatement(Guard(visited.Statement)), node);
		}

		public override SyntaxNode? VisitWhileStatement(
			WhileStatementSyntax node)
		{
			// Trusted fanout drains already checkpoint each emitted event.
			// Do not count these as authored CPU-only loop iterations.
			if (node.HasAnnotations(TrustedFanoutDrainAnnotation))
				return node;
			WhileStatementSyntax visited =
				(WhileStatementSyntax)(
					base.VisitWhileStatement(node)
						?? node);
			return WrapLoop(visited.WithStatement(Guard(visited.Statement)), node);
		}

		public override SyntaxNode? VisitDoStatement(
			DoStatementSyntax node)
		{
			DoStatementSyntax visited =
				(DoStatementSyntax)(
					base.VisitDoStatement(node)
						?? node);
			return WrapLoop(visited.WithStatement(Guard(visited.Statement)), node);
		}

		private StatementSyntax WrapLoop(StatementSyntax loop, StatementSyntax original)
		{
			if (!diagnostic) return loop;
			string span = original.GetAnnotations(DiagnosticLoopAnnotation).Single().Data
				?? throw new InvalidOperationException("Diagnostic loop source annotation missing.");
			return WrapScope(loop, "base.__HeresyEnterTraceLoop(" + span + ")");
		}

		private static StatementSyntax WrapScope(StatementSyntax statement, string expression)
		{
			var scope = (UsingStatementSyntax)SyntaxFactory.ParseStatement("using (" + expression + ") {}");
			return scope.WithStatement(SyntaxFactory.Block(statement.WithoutTrivia()))
				.WithTriviaFrom(statement);
		}

		private StatementSyntax Guard(
			StatementSyntax body)
		{
			StatementSyntax checkpoint = SyntaxFactory.ParseStatement(
				cooperative
					? "if (ShouldCooperate()) yield return CpuCheckpoint();"
					: "Checkpoint();");

			if (diagnostic)
			{
				StatementSyntax trace = SyntaxFactory.ParseStatement("base.__HeresyDiagnosticCheckpoint();");
				StatementSyntax guarded = body is BlockSyntax diagnosticBlock
					? diagnosticBlock.WithStatements(diagnosticBlock.Statements
						.Insert(0, checkpoint).Insert(0, trace))
					: SyntaxFactory.Block(trace, checkpoint, body.WithoutLeadingTrivia())
						.WithTriviaFrom(body);
				return WrapScope(guarded, "base.__HeresyEnterTraceIteration()");
			}
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
				bool publish = true;
				if (step is RawPatternStep.Emit emission)
				{
					// A script may accidentally emit an already-passed
					// musical position. It is too late to schedule that
					// event, so discard it without rewinding or reordering.
					if (emission.Row < lastRow)
					{
						context.Diagnostics.ReportDroppedOutOfOrderNote(
							emission.Row, lastRow);
						publish = false;
					}
					else
						lastRow = emission.Row;
				}
				else if (step is RawPatternStep.Advance advance
					&& advance.Row > lastRow)
				{
					lastRow = advance.Row;
				}
				if (publish)
					yield return step;
				// Dispose cannot yield in an authored finally/using
				// block. Publish trusted releases at the next boundary,
				// after that boundary's event (if any) has left the program.
				while (program.TryTakeVirtualChannelRelease(out var identity))
					yield return new RawPatternStep.ReleaseVirtualChannel(identity);
			}
			// Normal iterator completion may execute the last using/finally
			// without another authored musical or cooperation step.
			while (program.TryTakeVirtualChannelRelease(out var identity))
				yield return new RawPatternStep.ReleaseVirtualChannel(identity);
			yield return new RawPatternStep.Advance(_rowCount);
		}
	}

	/// <summary>
	/// A synchronous authored invocation, with no publication until it has
	/// completed successfully. Both raw consumer contracts replay the same
	/// stable musical-row ordering; wall deadlines remain scheduler-owned.
	/// </summary>
	private sealed class CompiledRelaxedPatternGenerator(
		Type programType, int rowCount, int channelCount)
		: IRawPatternNoteGenerator, IIncrementalRawPatternNoteGenerator
	{
		private NoteEvent[] Collect(SequencingContext context)
		{
			NoteScheduleBuilder receiver = new();
			PatternScriptProgram program =
				Activator.CreateInstance(programType, context, receiver,
					(double)rowCount, channelCount) as PatternScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the relaxed pattern script.");

			// A late emission may belong to row zero. Do not yield or
			// publish anything from the authored invocation before it returns.
			program.ExecuteRelaxed();
			NoteEvent[] events = receiver.Freeze().ToArray();
			Array.Sort(events, static (a, b) =>
			{
				int byRow = a.Offset.RowOffset.CompareTo(b.Offset.RowOffset);
				return byRow != 0 ? byRow : a.EmissionOrder.CompareTo(b.EmissionOrder);
			});
			context.Cancellation.ThrowIfCancellationRequested();
			return events;
		}

		public void GenerateRawNotes(SequencingContext context, INoteReceiver output,
			out double generatedRowCount)
		{
			ArgumentNullException.ThrowIfNull(context);
			ArgumentNullException.ThrowIfNull(output);
			generatedRowCount = rowCount;
			foreach (NoteEvent note in Collect(context))
				output.Append(note);
		}

		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			ArgumentNullException.ThrowIfNull(context);
			foreach (RawPatternStep step in RelaxedVirtualChannelReplay.Enumerate(Collect(context)))
				yield return step;
			yield return new RawPatternStep.Advance(rowCount);
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
