using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Scripting;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Heresy.Scripting.Analysis;

public enum ScriptDiagnosticSeverity
{
	Info,
	Warning,
	Error,
}

public readonly record struct ScriptSourceSpan(int Start, int Length)
{
	public int End => Start + Length;
}

public sealed record ScriptObjectReference(
	ObjectId Id,
	ScriptSourceSpan Span);

public sealed record ScriptAnalysisDiagnostic(
	string Code,
	ScriptDiagnosticSeverity Severity,
	string Message,
	ScriptSourceSpan Span);

public sealed record ScriptReferenceAnalysisSnapshot(
	SourceText Text,
	SyntaxTree SyntaxTree,
	ScriptReferenceAnalysis Analysis);

public sealed record ScriptReferenceAnalysis(
	IReadOnlyList<ScriptObjectReference> References,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics,
	IReadOnlyList<ScriptSourceSpan>? DynamicReferences = null)
{
	/// <summary>Computed _O(expression) calls remain legal script text but
	/// cannot become name tokens or participate in static deep duplication.</summary>
	public bool HasDynamicReferences => DynamicReferences is { Count: > 0 };

	public bool IsReliable
	{
		get
		{
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
/// Roslyn host surface used only to give the analyzer a real semantic symbol
/// for Heresy's persisted <c>_O(id)</c> intrinsic. Script execution will use a
/// separate restricted host/compiler pipeline.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptAnalysisGlobals
{
	public object _O(uint id)
		=> throw new NotSupportedException(
			$"Object lookup {id} is an analysis-only intrinsic.");

	// Mirror the runtime overloads so Roslyn resolves _O(int) variables
	// and ordinary signed literals as intrinsic calls, not unknown methods.
	public object _O(int id)
		=> _O(unchecked((uint)id));
}

/// <summary>
/// Finds semantic Heresy object-reference expressions in restricted-C# source.
/// Trivia and literals are handled by Roslyn rather than by textual scanning,
/// and a user declaration that shadows <c>_O</c> is not mistaken for the
/// Heresy intrinsic.
/// </summary>
public static class ScriptReferenceAnalyzer
{
	private const string MalformedReferenceCode = "HRS1001";

	private static readonly CSharpParseOptions ParseOptions =
		new(
			LanguageVersion.Preview,
			kind: SourceCodeKind.Script);

	private static readonly MetadataReference[] MetadataReferences =
	[
		MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(ScriptAnalysisGlobals).Assembly.Location),
	];

	public static ScriptReferenceAnalysis Analyze(string source)
		=> CreateSnapshot(source).Analysis;

	public static ScriptReferenceAnalysisSnapshot CreateSnapshot(
		string source,
		ScriptReferenceAnalysisSnapshot? previous = null)
	{
		ArgumentNullException.ThrowIfNull(source);

		SourceText sourceText =
			SourceText.From(source);
		SyntaxTree syntaxTree =
			previous is null
				? CSharpSyntaxTree.ParseText(
					sourceText,
					ParseOptions)
				: previous.SyntaxTree.WithChangedText(
					sourceText);

		// Keep the original syntax tree for incremental editing and syntax
		// highlighting. Only semantic reference analysis uses the masked
		// view, whose UTF-16 coordinates match the visible source exactly.
		// This is not execution authorization: the compiler decides which
		// script kinds and ordering modes are permitted.
		ScriptPatternOrderAnalysis order = ScriptPatternOrderDirective.Analyze(syntaxTree);
		SyntaxTree referenceTree = order.CompilerSource == source
			? syntaxTree
			: CSharpSyntaxTree.ParseText(SourceText.From(order.CompilerSource), ParseOptions);
		// At submission scope Roslyn can parse "using var" as an
		// import directive. Heresy executes method-body statements instead.
		// Prefer the block interpretation only when the original syntax
		// has errors and the complete block parses successfully.
		int sourceOffset = 0;
		if (referenceTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
		{
			SyntaxTree blockTree = CSharpSyntaxTree.ParseText(
				"{\n" + order.CompilerSource + "\n}", ParseOptions);
			if (!blockTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
			{
				referenceTree = blockTree;
				sourceOffset = 2;
			}
		}
		List<ScriptAnalysisDiagnostic> diagnostics = [.. order.Diagnostics];
		foreach (Diagnostic diagnostic in referenceTree.GetDiagnostics())
			diagnostics.Add(ProjectDiagnostic(diagnostic, sourceOffset));

		CSharpCompilation compilation =
			CSharpCompilation.CreateScriptCompilation(
				"Heresy.Script.ReferenceAnalysis",
				referenceTree,
				MetadataReferences,
				new CSharpCompilationOptions(
					OutputKind.DynamicallyLinkedLibrary),
				globalsType: typeof(ScriptAnalysisGlobals));
		SemanticModel semanticModel =
			compilation.GetSemanticModel(
				referenceTree,
				ignoreAccessibility: true);
		INamedTypeSymbol? globalsSymbol =
			compilation.GetTypeByMetadataName(
				typeof(ScriptAnalysisGlobals).FullName
					?? throw new InvalidOperationException(
						"Script analysis globals type has no metadata name."));

		List<ScriptObjectReference> references = [];
		List<ScriptSourceSpan> dynamicReferences = [];
		SyntaxNode root = referenceTree.GetRoot();
		foreach (InvocationExpressionSyntax invocation
			in root.DescendantNodes()
				.OfType<InvocationExpressionSyntax>())
		{
			if (invocation.Expression is not IdentifierNameSyntax identifier
				|| identifier.Identifier.ValueText
					!= ScriptObjectReferenceSyntax.LookupFunctionName)
			{
				continue;
			}

			SymbolInfo symbolInfo =
				semanticModel.GetSymbolInfo(invocation.Expression);
			if (!IsIntrinsicLookup(
					symbolInfo.Symbol,
					globalsSymbol)
				&& !symbolInfo.CandidateSymbols.Any(
					symbol => IsIntrinsicLookup(
						symbol,
						globalsSymbol)))
			{
				continue;
			}

			if (invocation.ContainsDiagnostics)
				continue;

			if (!TryGetObjectId(invocation, out ObjectId id))
			{
				// Computed IDs are valid source expressions if the script
				// compiler accepts their types. Only literal IDs become named
				// visual tokens; deep duplication separately refuses dynamic IDs.
				if (invocation.ArgumentList.Arguments.Count == 1
					&& invocation.ArgumentList.Arguments[0].Expression
						is not LiteralExpressionSyntax)
				{
					dynamicReferences.Add(new ScriptSourceSpan(
						invocation.SpanStart - sourceOffset, invocation.Span.Length));
					continue;
				}
				diagnostics.Add(
					new ScriptAnalysisDiagnostic(
						MalformedReferenceCode,
						ScriptDiagnosticSeverity.Error,
						"An object lookup needs one argument. Literal IDs must be non-zero 32-bit integers, for example _O(137); computed int and uint expressions are also allowed.",
						new ScriptSourceSpan(
							invocation.SpanStart - sourceOffset,
							invocation.Span.Length)));
				continue;
			}

			references.Add(
				new ScriptObjectReference(
					id,
					new ScriptSourceSpan(
						invocation.SpanStart,
						invocation.Span.Length)));
		}

		return new ScriptReferenceAnalysisSnapshot(
			sourceText,
			syntaxTree,
			new ScriptReferenceAnalysis(
				references,
				diagnostics,
				dynamicReferences));
	}

	private static bool IsIntrinsicLookup(
		ISymbol? symbol,
		INamedTypeSymbol? globalsSymbol)
		=> symbol is IMethodSymbol method
			&& globalsSymbol is not null
			&& SymbolEqualityComparer.Default.Equals(
				method.ContainingType,
				globalsSymbol)
			&& method.Name
				== ScriptObjectReferenceSyntax.LookupFunctionName;

	private static bool TryGetObjectId(
		InvocationExpressionSyntax invocation,
		out ObjectId id)
	{
		id = ObjectId.None;

		if (invocation.ArgumentList.Arguments.Count != 1)
			return false;

		ExpressionSyntax expression =
			invocation.ArgumentList.Arguments[0].Expression;
		if (expression is not LiteralExpressionSyntax literal
			|| !literal.IsKind(SyntaxKind.NumericLiteralExpression)
			|| !TryConvertToUInt32(
				literal.Token.Value,
				out uint value)
			|| value == 0)
		{
			return false;
		}

		id = (ObjectId)value;
		return true;
	}

	private static bool TryConvertToUInt32(
		object? value,
		out uint result)
	{
		switch (value)
		{
			case int signed when signed >= 0:
				result = (uint)signed;
				return true;

			case uint unsigned:
				result = unsigned;
				return true;

			case long signedLong when signedLong is >= 0 and <= uint.MaxValue:
				result = (uint)signedLong;
				return true;

			case ulong unsignedLong when unsignedLong <= uint.MaxValue:
				result = (uint)unsignedLong;
				return true;

			default:
				result = 0;
				return false;
		}
	}

	private static ScriptAnalysisDiagnostic ProjectDiagnostic(
		Diagnostic diagnostic, int sourceOffset = 0)
	{
		Microsoft.CodeAnalysis.Text.TextSpan sourceSpan =
			diagnostic.Location.IsInSource
				? diagnostic.Location.SourceSpan
				: default;

		return new ScriptAnalysisDiagnostic(
			diagnostic.Id,
			diagnostic.Severity switch
			{
				DiagnosticSeverity.Warning =>
					ScriptDiagnosticSeverity.Warning,
				DiagnosticSeverity.Error =>
					ScriptDiagnosticSeverity.Error,
				_ => ScriptDiagnosticSeverity.Info,
			},
			diagnostic.GetMessage(),
			new ScriptSourceSpan(
				Math.Max(0, sourceSpan.Start - sourceOffset),
				sourceSpan.Length));
	}
}
