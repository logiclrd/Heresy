using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

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

	public static ScriptCompilationResult<INoteSequencer> CompileSequence(
		ScriptSequenceDefinition definition,
		ISequencePatternResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(resolver);

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
				resolver),
			compiled.Diagnostics);
	}

	private static ScriptCompilationResult<Type> CompileType(
		string source,
		string className,
		string wrapperSource,
		Type requiredBaseType)
	{
		ScriptReferenceAnalysis referenceAnalysis =
			ScriptReferenceAnalyzer.Analyze(source);
		List<ScriptAnalysisDiagnostic> diagnostics =
			[.. referenceAnalysis.Diagnostics];

		if (!referenceAnalysis.IsReliable)
			return new(null, diagnostics);

		diagnostics.AddRange(ValidateRestrictedSyntax(source));
		if (HasErrors(diagnostics))
			return new(null, diagnostics);

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

		if (HasErrors(diagnostics))
			return new(null, diagnostics);

		diagnostics.AddRange(
			ValidateRestrictedSemantics(
				compilation,
				syntaxTree,
				source));
		if (HasErrors(diagnostics))
			return new(null, diagnostics);

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

	private static IReadOnlyList<ScriptAnalysisDiagnostic>
		ValidateRestrictedSyntax(string source)
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
					AnonymousFunctionExpressionSyntax =>
						"Lambdas and anonymous functions are not available in Heresy scripts.",
					ObjectCreationExpressionSyntax
						or ImplicitObjectCreationExpressionSyntax
						or AnonymousObjectCreationExpressionSyntax =>
						"Object creation is not available in Heresy scripts.",
					ArrayCreationExpressionSyntax
						or ImplicitArrayCreationExpressionSyntax
						or StackAllocArrayCreationExpressionSyntax =>
						"Array allocation is not available in Heresy scripts.",
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
					ForEachStatementSyntax
						or ForEachVariableStatementSyntax =>
						"foreach is not available in the initial restricted scripting surface.",
					QueryExpressionSyntax =>
						"LINQ query syntax is not available in Heresy scripts.",
					TypeOfExpressionSyntax =>
						"Runtime type access is not available in Heresy scripts.",
					ConditionalAccessExpressionSyntax =>
						"Conditional member access is not available in the restricted scripting surface.",
					ThisExpressionSyntax
						or BaseExpressionSyntax =>
						"Direct access to the generated script object is not available.",
					InterpolatedStringExpressionSyntax =>
						"String construction is not available in Heresy scripts.",
					LiteralExpressionSyntax literal
						when literal.IsKind(
							SyntaxKind.StringLiteralExpression) =>
						"String values are not available in Heresy scripts.",
					UsingStatementSyntax =>
						"using statements are not available in Heresy scripts.",
					LocalDeclarationStatementSyntax declaration
						when declaration.UsingKeyword.RawKind != 0 =>
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
			string source)
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
					mathType))
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
			if (IsAllowedMathMember(symbol, mathType))
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
		INamedTypeSymbol? mathType)
	{
		INamedTypeSymbol containingType = method.ContainingType;
		return SymbolEqualityComparer.Default.Equals(
				containingType,
				patternBase)
			|| SymbolEqualityComparer.Default.Equals(
				containingType,
				sequenceBase)
			|| SymbolEqualityComparer.Default.Equals(
				containingType,
				mathType);
	}

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

	private static string BuildPatternWrapper(
		string className,
		string source)
		=> $$"""
			using System;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;

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

	private static string BuildSequenceWrapper(
		string className,
		string source)
		=> $$"""
			using System;
			using Heresy.Core.Sequencing;
			using Heresy.Scripting.Runtime;

			public sealed class {{className}} : SequenceScriptProgram
			{
				public {{className}}(SequencingContext context)
					: base(context)
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

	private sealed class LoopCheckpointRewriter : CSharpSyntaxRewriter
	{
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

		private static StatementSyntax Guard(
			StatementSyntax body)
		{
			ExpressionStatementSyntax checkpoint =
				SyntaxFactory.ExpressionStatement(
					SyntaxFactory.InvocationExpression(
						SyntaxFactory.IdentifierName(
							"Checkpoint")));

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
					output,
					(double)_rowCount,
					_channelCount)
					as PatternScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the compiled pattern script.");
			program.Execute();
		}
	}

	private sealed class CompiledSequenceSequencer
		: INoteSequencer
	{
		private readonly Type _programType;
		private readonly ISequencePatternResolver _resolver;

		public CompiledSequenceSequencer(
			Type programType,
			ISequencePatternResolver resolver)
		{
			_programType = programType;
			_resolver = resolver;
		}

		public void GenerateNotes(
			SequencingContext context,
			INoteReceiver output,
			out TimeSpan duration)
		{
			ArgumentNullException.ThrowIfNull(context);
			ArgumentNullException.ThrowIfNull(output);

			SequenceScriptProgram program =
				Activator.CreateInstance(
					_programType,
					context)
					as SequenceScriptProgram
				?? throw new InvalidOperationException(
					"Could not construct the compiled sequence script.");

			IReadOnlyList<SequenceEntry> entries =
				program.Execute();
			SequenceNoteProcessor.GenerateNotes(
				entries,
				_resolver,
				context,
				output,
				out duration);
		}
	}
}
