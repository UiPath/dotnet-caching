using System.Collections.Immutable;
using System.Composition;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace UiPath.Caching.Analyzers;

/// <summary>Fixes CS0121 and CS8952 on a UiPath.Caching interpolated key: a plain string for a constant one, <c>FormattableString.Invariant</c> in an expression tree.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(InterpolatedKeyCodeFixProvider))]
[Shared]
public sealed class InterpolatedKeyCodeFixProvider : CodeFixProvider
{
    private const string Ambiguous = "CS0121";
    private const string PlainStringTitle = "Use a plain string key";
    private const string InvariantTitle = "Format the key with FormattableString.Invariant";

    // Spaced here: the formatter leaves none between a hole's trailing comment and the operator.
    private static readonly SyntaxToken Plus = SyntaxFactory.Token(SyntaxKind.PlusToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space);

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(Ambiguous, "CS8952");

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
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (Fix(model, node, diagnostic.Id, context.CancellationToken) is not { } fix)
            {
                continue;
            }

            var (key, replacement, title) = fix;
            var document = context.Document;
            context.RegisterCodeFix(
                CodeAction.Create(title, _ => Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(key, replacement))), title),
                diagnostic);
        }
    }

    private static (InterpolatedStringExpressionSyntax Key, ExpressionSyntax Replacement, string Title)? Fix(SemanticModel model, SyntaxNode node, string id, CancellationToken token)
    {
        if (id == Ambiguous)
        {
            // On the call's own name: a CS0121 nested in its arguments, such as an ambiguous constructor, is not about the key.
            if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } invocation
                || !invocation.Expression.Span.Contains(node.Span)
                || ConstantKey(model, invocation, token) is not var (key, value)
                || PlainString(model, key, value, token) is not { } plain)
            {
                return null;
            }

            // Only when the key is what makes the call ambiguous: a plain string must leave one method to call.
            var resolved = model.GetSpeculativeSymbolInfo(invocation.SpanStart, invocation.ReplaceNode(key, plain), SpeculativeBindingOption.BindAsExpression);
            return resolved.Symbol is IMethodSymbol ? (key, plain, PlainStringTitle) : null;
        }

        if (node.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>() is not { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } list } argument } tree
            || !TakesHandler(model.GetSymbolInfo(call, token), argument, list.Arguments.IndexOf(argument)))
        {
            return null;
        }

        // A named argument is not allowed in an expression tree either (CS0853), so no rewrite of the key makes it compile.
        if (list.Arguments.Any(a => a.NameColon is not null))
        {
            return null;
        }

        // A constant key reads better as a plain string than wrapped.
        if (model.GetConstantValue(tree, token) is { HasValue: true, Value: string constant })
        {
            return PlainString(model, tree, constant, token) is { } plain ? (tree, plain, PlainStringTitle) : null;
        }

        // A span cannot be a FormattableString argument, so the wrap would not compile either.
        return HasRefLikeHole(model, tree, token) ? null : (tree, Invariant(tree), InvariantTitle);
    }

    private static bool HasRefLikeHole(SemanticModel model, InterpolatedStringExpressionSyntax key, CancellationToken token) =>
        key.Contents.OfType<InterpolationSyntax>().Any(hole => model.GetTypeInfo(hole.Expression, token).Type is { IsRefLikeType: true });

    // Fully qualified, then simplified only as far as the names in scope allow, so a local or type named FormattableString cannot capture it.
    private static ExpressionSyntax Invariant(InterpolatedStringExpressionSyntax key)
    {
        var invariant = SyntaxFactory.ParseExpression("global::System.FormattableString.Invariant").WithAdditionalAnnotations(Simplifier.Annotation);
        return SyntaxFactory.InvocationExpression(invariant, SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(key.WithoutTrivia()))))
            .WithTriviaFrom(key);
    }

    private static bool TakesHandler(SymbolInfo info, ArgumentSyntax argument, int position) =>
        (info.Symbol is { } symbol ? ImmutableArray.Create(symbol) : info.CandidateSymbols).Any(method => method is IMethodSymbol m && IsHandler(Parameter(m, argument, position)));

    /// <summary>The constant interpolated key of an ambiguous call, when a candidate takes it as a <c>CacheKeyHandler</c>.</summary>
    private static (InterpolatedStringExpressionSyntax Key, string Value)? ConstantKey(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken token)
    {
        var info = model.GetSymbolInfo(invocation, token);
        // CS0121 already says the call is ambiguous; Roslyn reports its candidates as an overload resolution failure.
        if (info.Symbol is not null)
        {
            return null;
        }

        var arguments = invocation.ArgumentList.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Expression is InterpolatedStringExpressionSyntax key
                && model.GetConstantValue(key, token) is { HasValue: true, Value: string value }
                && info.CandidateSymbols.Any(candidate => candidate is IMethodSymbol method && IsHandler(Parameter(method, arguments[i], i))))
            {
                return (key, value);
            }
        }

        return null;
    }

    private static IParameterSymbol? Parameter(IMethodSymbol method, ArgumentSyntax argument, int position)
    {
        if (argument.NameColon is { } name)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == name.Name.Identifier.ValueText);
        }

        return position < method.Parameters.Length ? method.Parameters[position] : null;
    }

    private static bool IsHandler(IParameterSymbol? parameter) =>
        parameter is { RefKind: RefKind.Ref, Type: INamedTypeSymbol { Name: "CacheKeyHandler" } type }
        && type.ContainingNamespace.ToDisplayString() == "UiPath.Caching";

    /// <summary>The key as a string literal, or as its parts joined with <c>+</c> so a hole that names a constant keeps it; null when the parts do not rebuild the key's text.</summary>
    private static ExpressionSyntax? PlainString(SemanticModel model, InterpolatedStringExpressionSyntax key, string value, CancellationToken token)
    {
        if (!key.Contents.Any(content => content is InterpolationSyntax))
        {
            return Literal(value).WithTriviaFrom(key).WithAdditionalAnnotations(Formatter.Annotation);
        }

        // A regular or verbatim interpolation writes a brace as {{ or }}, which the text token keeps; a raw one never doubles them.
        var raw = key.StringStartToken.Text.EndsWith("\"\"\"", StringComparison.Ordinal);
        var rebuilt = new StringBuilder();
        ExpressionSyntax? joined = null;
        foreach (var content in key.Contents)
        {
            ExpressionSyntax? operand = null;
            if (content is InterpolatedStringTextSyntax text)
            {
                var part = raw ? text.TextToken.ValueText : text.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}");
                rebuilt.Append(part);
                operand = part.Length > 0 ? Literal(part) : null;
            }
            else if (content is InterpolationSyntax hole)
            {
                if (model.GetConstantValue(hole.Expression, token) is not { HasValue: true, Value: string part })
                {
                    return null;
                }

                rebuilt.Append(part);
                // A nested constant interpolation is flattened too: left as it is, it would still be ambiguous.
                operand = hole.Expression is InterpolatedStringExpressionSyntax nested
                    ? PlainString(model, nested, part, token) is { } flat ? Operand(flat) : null
                    : Operand(hole.Expression);
                if (operand is null)
                {
                    return null;
                }
            }

            if (operand is not null)
            {
                joined = joined is null ? operand : SyntaxFactory.BinaryExpression(SyntaxKind.AddExpression, joined, Plus, operand);
            }
        }

        // The anti-goal: a rewrite never changes the key text.
        if (rebuilt.ToString() != value)
        {
            return null;
        }

        return (joined ?? Literal(value)).WithTriviaFrom(key).WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static ExpressionSyntax Operand(ExpressionSyntax hole) =>
        hole is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or LiteralExpressionSyntax or InterpolatedStringExpressionSyntax or ParenthesizedExpressionSyntax
            ? hole
            : SyntaxFactory.ParenthesizedExpression(hole.WithoutTrivia()).WithTriviaFrom(hole);

    private static LiteralExpressionSyntax Literal(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));
}
