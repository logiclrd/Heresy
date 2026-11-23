using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Heresy.Scripting.Compilation;

/// <summary>A finding against the author's original syntax, never a string-based
/// approximation of the referenced API's name.</summary>
internal sealed record ScriptRuntimeOperationFinding(
    TextSpan Span, string Reason);

/// <summary>
/// Read-only, deny-by-default *prototype* for the next Pattern/Sequence
/// compiler stage. Resolves executable operations to Roslyn symbols, including
/// implicit foreach calls and method-group targets. It is intentionally NOT
/// wired into ScriptCompiler: entire-source coverage, generated wrapper trust
/// boundaries, diagnostics and incremental rewrites must be verified before
/// it becomes an authoring gate.
/// </summary>
internal sealed class ScriptRuntimeOperationVerifier
{
    private readonly SemanticModel _model;
    private readonly Microsoft.CodeAnalysis.Compilation _compilation;
    private readonly ScriptRuntimeApiPolicy _policy;
    private readonly List<ScriptRuntimeOperationFinding> _findings = [];

    private ScriptRuntimeOperationVerifier(SemanticModel model,
        ScriptRuntimeApiPolicy policy)
    {
        _model = model;
        _compilation = model.Compilation;
        _policy = policy;
    }

    /// <summary>
    /// Examine one bound executable scope (usually a method body) and its type
    /// syntax. A null operation is a hard error, never silent acceptance.
    /// </summary>
    internal static IReadOnlyList<ScriptRuntimeOperationFinding> Analyze(
        SemanticModel model, SyntaxNode executableScope,
        ScriptRuntimeApiPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(executableScope);
        IOperation root = model.GetOperation(executableScope)
            ?? throw new InvalidOperationException(
                "The requested script scope did not bind to a Roslyn operation.");

        ScriptRuntimeOperationVerifier visitor =
            new(model, policy ?? ScriptRuntimeApiPolicy.Default);
        visitor.Visit(root);
        visitor.CheckTypeSyntax(executableScope);
        return visitor._findings.Distinct().ToArray();
    }

    /// <summary>
    /// Inspect every authored executable declaration in one C# source tree,
    /// rather than just an explicitly selected method body. This is a
    /// pre-integration audit: the production compiler still retains its
    /// existing restricted rules. A multi-tree input requires a separately
    /// designed trust boundary and is rejected rather than partly inspected.
    /// </summary>
    internal static IReadOnlyList<ScriptRuntimeOperationFinding> AnalyzeSource(
        CSharpCompilation compilation, ScriptRuntimeApiPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        SyntaxTree[] trees = compilation.SyntaxTrees.ToArray();
        if (trees.Length != 1)
            throw new InvalidOperationException(
                "The source audit requires exactly one authored C# tree.");

        SyntaxTree tree = trees[0];
        SyntaxNode root = tree.GetRoot();
        ScriptRuntimeOperationVerifier visitor = new(
            compilation.GetSemanticModel(tree),
            policy ?? ScriptRuntimeApiPolicy.Default);

        foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree == tree)
                visitor.Add(diagnostic.Location.SourceSpan,
                    "Unresolved C# source: " + diagnostic.Id);
        }

        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
                visitor.Add(trivia.Span, "Source directives are not approved.");
        }

        // Every source declaration is visited. A declaration that can contain
        // executable code must have its body or initializer bound separately;
        // visiting only the entry method misses static and field initializers,
        // lambda bodies, local functions and expression-bodied members.
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    visitor.AuditBody(method.Body, method.ExpressionBody?.Expression);
                    visitor.RejectExtern(method.Modifiers, method);
                    break;
                case ConstructorDeclarationSyntax constructor:
                    visitor.AuditBody(constructor.Body,
                        constructor.ExpressionBody?.Expression);
                    visitor.RejectExtern(constructor.Modifiers, constructor);
                    if (constructor.Initializer is { } initializer)
                    {
                        ISymbol? target = visitor._model.GetSymbolInfo(initializer).Symbol;
                        if (target is IMethodSymbol methodSymbol)
                            visitor.CheckMember(methodSymbol, ScriptMemberUse.Construct,
                                initializer);
                        else
                            visitor.Add(initializer,
                                "Constructor initializer did not bind to a method.");
                        foreach (ArgumentSyntax arg in initializer.ArgumentList.Arguments)
                            visitor.AuditExecutable(arg.Expression);
                    }
                    break;
                case AccessorDeclarationSyntax accessor:
                    visitor.AuditBody(accessor.Body,
                        accessor.ExpressionBody?.Expression);
                    break;
                case OperatorDeclarationSyntax op:
                    visitor.AuditBody(op.Body, op.ExpressionBody?.Expression);
                    break;
                case ConversionOperatorDeclarationSyntax conversion:
                    visitor.AuditBody(conversion.Body,
                        conversion.ExpressionBody?.Expression);
                    break;
                case DestructorDeclarationSyntax destructor:
                    visitor.AuditBody(destructor.Body,
                        destructor.ExpressionBody?.Expression);
                    if (visitor._model.GetDeclaredSymbol(destructor) is { } finalizer
                        && visitor._policy.IsBlockedMember(finalizer,
                            ScriptMemberUse.CallOrMethodGroup,
                            visitor._compilation))
                        visitor.Add(destructor, "User-defined finalizers are blocked.");
                    break;
                case LocalFunctionStatementSyntax local:
                    visitor.AuditBody(local.Body, local.ExpressionBody?.Expression);
                    visitor.RejectExtern(local.Modifiers, local);
                    break;
                case SimpleLambdaExpressionSyntax lambda:
                    visitor.AuditExecutable(lambda.Body);
                    break;
                case ParenthesizedLambdaExpressionSyntax lambda:
                    visitor.AuditExecutable(lambda.Body);
                    break;
                case AnonymousMethodExpressionSyntax anonymous:
                    visitor.AuditExecutable(anonymous.Block);
                    break;
                case PropertyDeclarationSyntax property:
                    visitor.AuditExecutable(property.ExpressionBody?.Expression);
                    visitor.AuditExecutable(property.Initializer?.Value);
                    break;
                case IndexerDeclarationSyntax indexer:
                    visitor.AuditExecutable(indexer.ExpressionBody?.Expression);
                    break;
                case EventDeclarationSyntax @event:
                    visitor.AuditExecutable(@event.ExpressionBody?.Expression);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
                        visitor.AuditExecutable(v.Initializer?.Value);
                    break;
                case EventFieldDeclarationSyntax field:
                    foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
                        visitor.AuditExecutable(v.Initializer?.Value);
                    break;
                case ParameterSyntax parameter:
                    visitor.AuditExecutable(parameter.Default?.Value);
                    break;
                case GlobalStatementSyntax statement:
                    visitor.AuditExecutable(statement.Statement);
                    break;
                case AttributeListSyntax attribute:
                    visitor.Add(attribute, "User-authored attributes require a separate review.");
                    break;
                case RecordDeclarationSyntax record:
                    visitor.Add(record,
                        "Record generated members require a separate audit.");
                    break;
                case UnsafeStatementSyntax unsafeStatement:
                    visitor.Add(unsafeStatement, "Unsafe code is not approved.");
                    break;
                case PointerTypeSyntax pointer:
                    visitor.Add(pointer, "Pointer types are not approved.");
                    break;
                case FunctionPointerTypeSyntax functionPointer:
                    visitor.Add(functionPointer, "Function pointers are not approved.");
                    break;
            }
        }

        visitor.CheckTypeSyntax(root);
        return visitor._findings.Distinct().OrderBy(x => x.Span.Start).ToArray();
    }

    private void AuditBody(BlockSyntax? body, ExpressionSyntax? expression)
    {
        AuditExecutable(body);
        AuditExecutable(expression);
    }

    private void AuditExecutable(SyntaxNode? executable)
    {
        if (executable is null)
            return;
        IOperation? operation = _model.GetOperation(executable);
        if (operation is null)
        {
            Add(executable, "Executable source could not be bound.");
            return;
        }
        Visit(operation);
    }

    private void RejectExtern(SyntaxTokenList modifiers, SyntaxNode node)
    {
        if (modifiers.Any(SyntaxKind.ExternKeyword)
            || modifiers.Any(SyntaxKind.UnsafeKeyword))
            Add(node, "Extern or unsafe declarations are not approved.");
    }

    private void Visit(IOperation operation)
    {
        if (operation.Kind == OperationKind.Invalid)
            Add(operation.Syntax, "Invalid or unresolved operation.");
        if (operation.Type is { } type)
            CheckType(type, operation.Syntax);

        switch (operation)
        {
            case IInvocationOperation invoke:
                CheckMember(invoke.TargetMethod, ScriptMemberUse.CallOrMethodGroup,
                    invoke.Syntax);
                break;
            case IObjectCreationOperation creation:
                if (creation.Constructor is { } constructor)
                    CheckMember(constructor, ScriptMemberUse.Construct,
                        creation.Syntax);
                else
                    Add(creation.Syntax, "Object constructor could not be resolved.");
                break;
            case IMethodReferenceOperation reference:
                CheckMember(reference.Method, ScriptMemberUse.CallOrMethodGroup,
                    reference.Syntax);
                break;
            case IPropertyReferenceOperation property:
                CheckMember(property.Property,
                    IsWrite(property) ? ScriptMemberUse.Write : ScriptMemberUse.Read,
                    property.Syntax);
                break;
            case IFieldReferenceOperation field:
                CheckMember(field.Field, IsWrite(field)
                    ? ScriptMemberUse.Write : ScriptMemberUse.Read, field.Syntax);
                break;
            case IEventReferenceOperation @event:
                CheckMember(@event.Event, ScriptMemberUse.Read, @event.Syntax);
                break;
            case IConversionOperation conversion
                when conversion.OperatorMethod is { } method:
                CheckMember(method, ScriptMemberUse.CallOrMethodGroup,
                    conversion.Syntax);
                break;
            case IBinaryOperation binary when binary.OperatorMethod is { } method:
                CheckMember(method, ScriptMemberUse.CallOrMethodGroup,
                    binary.Syntax);
                break;
            case IUnaryOperation unary when unary.OperatorMethod is { } method:
                CheckMember(method, ScriptMemberUse.CallOrMethodGroup,
                    unary.Syntax);
                break;
            case ICompoundAssignmentOperation compound
                when compound.OperatorMethod is { } method:
                CheckMember(method, ScriptMemberUse.CallOrMethodGroup,
                    compound.Syntax);
                break;
            case IIncrementOrDecrementOperation increment
                when increment.OperatorMethod is { } method:
                CheckMember(method, ScriptMemberUse.CallOrMethodGroup,
                    increment.Syntax);
                break;
            case IForEachLoopOperation loop:
                CheckForEach(loop);
                break;
            case ITypeOfOperation:
                Add(operation.Syntax, "Runtime type objects are unavailable.");
                break;
            case IAwaitOperation or ILockOperation
                or IDynamicInvocationOperation or IAddressOfOperation
                or IAnonymousObjectCreationOperation:
                Add(operation.Syntax,
                    "Execution or object form is not approved for script isolation.");
                break;
        }

        foreach (IOperation child in operation.ChildOperations)
            Visit(child);
    }

    private void CheckForEach(IForEachLoopOperation loop)
    {
        if (loop.Syntax is not CommonForEachStatementSyntax syntax)
        {
            Add(loop.Syntax, "Unrecognized foreach binding.");
            return;
        }

        ForEachStatementInfo info = _model.GetForEachStatementInfo(syntax);
        CheckMember(info.GetEnumeratorMethod, ScriptMemberUse.CallOrMethodGroup,
            syntax);
        CheckMember(info.MoveNextMethod, ScriptMemberUse.CallOrMethodGroup,
            syntax);
        CheckMember(info.CurrentProperty, ScriptMemberUse.Read, syntax);
        CheckMember(info.DisposeMethod, ScriptMemberUse.CallOrMethodGroup,
            syntax);
        if (info.ElementType is { } type)
            CheckType(type, syntax);
    }

    private void CheckTypeSyntax(SyntaxNode scope)
    {
        // Operations don't expose every type-only reference (for example a
        // named generic argument or a cast target). Check the semantic symbol
        // independently, not a spelling-based namespace prefix.
        foreach (TypeSyntax syntax in scope.DescendantNodesAndSelf()
            .OfType<TypeSyntax>())
        {
            ITypeSymbol? type = _model.GetTypeInfo(syntax).Type;
            if (type is not null)
                CheckType(type, syntax);
        }

        if (scope.DescendantNodesAndSelf().OfType<StackAllocArrayCreationExpressionSyntax>()
            .Any())
        {
            foreach (StackAllocArrayCreationExpressionSyntax allocation
                in scope.DescendantNodesAndSelf()
                    .OfType<StackAllocArrayCreationExpressionSyntax>())
                Add(allocation, "Stack allocation is not allowed.");
        }
    }

    private void CheckMember(ISymbol? member, ScriptMemberUse use,
        SyntaxNode syntax)
    {
        if (member is null)
            return; // e.g. arrays/strings may have no enumerator method.

        if (_policy.IsBlockedMember(member, use, _compilation))
        {
            Add(syntax, "Blocked runtime member: " + member.ToDisplayString());
            return;
        }

        if (member is IMethodSymbol method)
        {
            foreach (ITypeSymbol type in method.TypeArguments)
                CheckType(type, syntax);
            CheckType(method.ReturnType, syntax);
        }
        else if (member is IPropertySymbol property)
            CheckType(property.Type, syntax);
        else if (member is IFieldSymbol field)
            CheckType(field.Type, syntax);
        else if (member is IEventSymbol eventSymbol)
            CheckType(eventSymbol.Type, syntax);

        if (member.ContainingType is { } containing)
            CheckType(containing, syntax);
    }

    private void CheckType(ITypeSymbol type, SyntaxNode syntax)
    {
        if (type is ITypeParameterSymbol)
            return; // Source type-parameter constraints need a separate audit.

        // User-defined types are only usable once all their declarations and
        // executable bodies are checked in a full-assembly validation pass.
        // This probe reports their use as requiring that further review.
        if (SymbolEqualityComparer.Default.Equals(
            type.ContainingAssembly, _compilation.Assembly))
        {
            if (type is INamedTypeSymbol named && named.IsAnonymousType)
                Add(syntax, "Anonymous generated types require further review.");
            return;
        }

        if (!_policy.IsAllowedRuntimeType(type, _compilation))
            Add(syntax, "Type outside approved runtime API: "
                + type.ToDisplayString());
    }

    private static bool IsWrite(IOperation reference)
    {
        // Skip parentheses and compiler-generated conversions around an
        // assignment target, but do not mistake a property used as an
        // object's *receiver* for the property being assigned.
        IOperation current = reference;
        while (current.Parent is IParenthesizedOperation
            or IConversionOperation { IsImplicit: true })
            current = current.Parent!;

        return current.Parent switch
        {
            IAssignmentOperation assignment
                when ReferenceEquals(assignment.Target, current) => true,
            IIncrementOrDecrementOperation increment
                when ReferenceEquals(increment.Target, current) => true,
            IArgumentOperation argument when argument.Parameter?.RefKind
                is RefKind.Ref or RefKind.Out => true,
            _ => false,
        };
    }

    private void Add(SyntaxNode syntax, string reason)
        => _findings.Add(new ScriptRuntimeOperationFinding(
            syntax.Span, reason));
}
