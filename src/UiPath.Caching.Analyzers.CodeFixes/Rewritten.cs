using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPath.Caching.Analyzers;

/// <summary>A call rewritten to pass state, and the name of the overload parameter its first state argument binds to.</summary>
internal sealed class Rewritten(InvocationExpressionSyntax original, InvocationExpressionSyntax call, string stateParameter)
{
    /// <summary>The call as written.</summary>
    public InvocationExpressionSyntax Original { get; } = original;

    /// <summary>The call that replaces it.</summary>
    public InvocationExpressionSyntax Call { get; } = call;

    /// <summary>The root with the call replaced.</summary>
    public SyntaxNode Apply(SyntaxNode root) => root.ReplaceNode(Original, Call);

    /// <summary>Whether the rewritten call, compiled in place of the original, has no error and binds to an overload that takes the state parameter: the last check before the fix is offered.</summary>
    public bool Compiles(SemanticModel model, CancellationToken cancellationToken)
    {
        var marker = new SyntaxAnnotation();
        var tree = Original.SyntaxTree;
        var changed = tree.WithRootAndOptions(tree.GetRoot(cancellationToken).ReplaceNode(Original, Call.WithAdditionalAnnotations(marker)), tree.Options);
        var changedModel = model.Compilation.ReplaceSyntaxTree(tree, changed).GetSemanticModel(changed);
        var rewrittenCall = changed.GetRoot(cancellationToken).GetAnnotatedNodes(marker).OfType<InvocationExpressionSyntax>().FirstOrDefault();
        return rewrittenCall is not null
            && changedModel.GetSymbolInfo(rewrittenCall, cancellationToken).Symbol is IMethodSymbol method
            && StateRewrite.TakesState(method, stateParameter)
            && !changedModel.GetDiagnostics(rewrittenCall.Span, cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
