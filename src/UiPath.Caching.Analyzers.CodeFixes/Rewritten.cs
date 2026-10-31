using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPath.Caching.Analyzers;

/// <summary>A call rewritten to pass state, and the snapshot locals it reads, declared just before the statement the call is in.</summary>
internal sealed class Rewritten(InvocationExpressionSyntax original, InvocationExpressionSyntax call, string stateParameter, ImmutableArray<LocalDeclarationStatementSyntax> snapshots)
{
    /// <summary>The call as written.</summary>
    public InvocationExpressionSyntax Original { get; } = original;

    /// <summary>The call that replaces it.</summary>
    public InvocationExpressionSyntax Call { get; } = call;

    /// <summary>The root with the call replaced and the snapshots declared.</summary>
    public SyntaxNode Apply(SyntaxNode root) => Apply(root, Call);

    /// <summary>Whether the rewritten call, compiled in place of the original, has no error and binds to an overload that takes the state parameter: the last check before the fix is offered.</summary>
    public bool Compiles(SemanticModel model, CancellationToken cancellationToken)
    {
        var marker = new SyntaxAnnotation();
        var tree = Original.SyntaxTree;
        var changed = tree.WithRootAndOptions(Apply(tree.GetRoot(cancellationToken), Call.WithAdditionalAnnotations(marker)), tree.Options);
        var changedModel = model.Compilation.ReplaceSyntaxTree(tree, changed).GetSemanticModel(changed);
        var rewrittenCall = changed.GetRoot(cancellationToken).GetAnnotatedNodes(marker).OfType<InvocationExpressionSyntax>().FirstOrDefault();
        return rewrittenCall is not null
            && changedModel.GetSymbolInfo(rewrittenCall, cancellationToken).Symbol is IMethodSymbol method
            && StateRewrite.TakesState(method, stateParameter)
            && !changedModel.GetDiagnostics(rewrittenCall.Span, cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private SyntaxNode Apply(SyntaxNode root, InvocationExpressionSyntax call)
    {
        if (snapshots.IsEmpty)
        {
            return root.ReplaceNode(Original, call);
        }

        var statement = Original.FirstAncestorOrSelf<StatementSyntax>()!;
        var leading = statement.GetLeadingTrivia();
        var indentation = SyntaxFactory.TriviaList(leading.Reverse().TakeWhile(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse());
        var lineEnd = root.DescendantTrivia().FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)) is { RawKind: not 0 } found ? found : SyntaxFactory.LineFeed;
        var statements = snapshots
            .Select((snapshot, i) => (StatementSyntax)snapshot.WithLeadingTrivia(i == 0 ? leading : indentation).WithTrailingTrivia(lineEnd))
            .Append(statement.ReplaceNode(Original, call).WithLeadingTrivia(indentation));
        return root.ReplaceNode(statement, statements);
    }
}
