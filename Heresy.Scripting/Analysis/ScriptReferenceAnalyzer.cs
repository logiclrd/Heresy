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
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
{
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

		List<ScriptAnalysisDiagnostic> diagnostics = [];
		foreach (Diagnostic diagnostic in syntaxTree.GetDiagnostics())
			diagnostics.Add(ProjectDiagnostic(diagnostic));

		CSharpCompilation compilation =
			CSharpCompilation.CreateScriptCompilation(
				"Heresy.Script.ReferenceAnalysis",
				syntaxTree,
				MetadataReferences,
				new CSharpCompilationOptions(
					OutputKind.DynamicallyLinkedLibrary),
				globalsType: typeof(ScriptAnalysisGlobals));
		SemanticModel semanticModel =
			compilation.GetSemanticModel(
				syntaxTree,
				ignoreAccessibility: true);
		INamedTypeSymbol? globalsSymbol =
			compilation.GetTypeByMetadataName(
				typeof(ScriptAnalysisGlobals).FullName
					?? throw new InvalidOperationException(
						"Script analysis globals type has no metadata name."));

		List<ScriptObjectReference> references = [];
		SyntaxNode root = syntaxTree.GetRoot();
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
				diagnostics.Add(
					new ScriptAnalysisDiagnostic(
						MalformedReferenceCode,
						ScriptDiagnosticSeverity.Error,
						"Object references must use one non-zero 32-bit integer literal, for example _O(137).",
						new ScriptSourceSpan(
							invocation.SpanStart,
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
				diagnostics));
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
		Diagnostic diagnostic)
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
				sourceSpan.Start,
				sourceSpan.Length));
	}
}
