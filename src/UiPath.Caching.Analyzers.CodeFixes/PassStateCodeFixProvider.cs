using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPath.Caching.Analyzers;

/// <summary>Fixes UIPCACHE001: moves the call to the state overload, passing what the generator captured as the state of a static lambda.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PassStateCodeFixProvider))]
[Shared]
public sealed class PassStateCodeFixProvider : CodeFixProvider
{
    private const string Title = "Pass captured values as state";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(CapturingGeneratorAnalyzer.DiagnosticId);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(CapturingGeneratorAnalyzer.StateParameterProperty, out var stateParameter)
                || stateParameter is null
                || root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<ArgumentSyntax>() is not { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } argument
                || StateRewrite.Rewrite(model, argument, stateParameter, diagnostic.Properties.ContainsKey(CapturingGeneratorAnalyzer.StateTypeIsLastProperty), context.CancellationToken) is not { } rewritten)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(Title, _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(invocation, rewritten))), equivalenceKey: Title),
                diagnostic);
        }
    }
}
