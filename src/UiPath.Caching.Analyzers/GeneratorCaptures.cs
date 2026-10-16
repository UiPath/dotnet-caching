using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UiPath.Caching.Analyzers;

/// <summary>What a generator lambda captures.</summary>
internal static class GeneratorCaptures
{
    /// <summary>The variables declared outside <paramref name="lambda"/> that it uses, directly or through local functions, <c>this</c> first.</summary>
    public static ImmutableArray<ISymbol> Of(IAnonymousFunctionOperation lambda)
    {
        if (lambda.SemanticModel is not { } model || lambda.Syntax is not ExpressionSyntax syntax || model.AnalyzeDataFlow(syntax) is not { Succeeded: true } flow)
        {
            return ImmutableArray<ISymbol>.Empty;
        }

        var found = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        Collect(model, lambda.Body, syntax, flow.CapturedInside, found, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        return Ordered(found);
    }

    /// <summary>What the local function that <paramref name="group"/> converts captures, itself or through the local functions it calls.</summary>
    public static ImmutableArray<ISymbol> OfLocalFunction(IMethodReferenceOperation group)
    {
        if (group.SemanticModel is not { } model)
        {
            return ImmutableArray<ISymbol>.Empty;
        }

        var found = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        Collect(model, group, group.Syntax, Enumerable.Empty<ISymbol>(), found, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        return Ordered(found);
    }

    /// <summary>Whether <paramref name="lambda"/> calls or converts a non-static local function declared outside it.</summary>
    public static bool UsesOuterLocalFunction(IAnonymousFunctionOperation lambda) =>
        Uses(lambda.Body).LocalFunctions.Any(function => !function.IsStatic && !DeclaredWithin(function, lambda.Syntax));

    public static bool IsThis(ISymbol symbol) => symbol is IParameterSymbol { IsThis: true };

    /// <summary>Whether <paramref name="symbol"/> is a primary constructor parameter, which a member reaches through <c>this</c>, as it does a field.</summary>
    public static bool IsPrimaryConstructorParameter(ISymbol symbol) =>
        symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } constructor }
        && constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax);

    public static string Describe(ISymbol symbol) => IsThis(symbol) ? "this" : symbol.Name;

    // Keeps the candidates `body` really uses, then follows the local functions declared outside `scope` that it calls.
    private static void Collect(SemanticModel model, IOperation body, SyntaxNode scope, IEnumerable<ISymbol> candidates, HashSet<ISymbol> found, HashSet<ISymbol> visited)
    {
        var uses = Uses(body);
        // A query's range variable reaches the operations as the parameter of a lambda the compiler makes, so it counts as data flow says.
        found.UnionWith(candidates.Where(symbol => !DeclaredWithin(symbol, scope) && (IsThis(symbol) ? uses.This : symbol is IRangeVariableSymbol || uses.Symbols.Contains(symbol))));
        foreach (var function in uses.LocalFunctions)
        {
            if (function.IsStatic
                || DeclaredWithin(function, scope)
                || !visited.Add(function)
                || function.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not LocalFunctionStatementSyntax declaration
                || model.GetOperation(declaration) is not ILocalFunctionOperation { Body: { } functionBody })
            {
                continue;
            }

            var flow = declaration.Body is { } block ? model.AnalyzeDataFlow(block) : model.AnalyzeDataFlow(declaration.ExpressionBody!.Expression);
            if (flow is { Succeeded: true })
            {
                Collect(model, functionBody, declaration, flow.ReadInside.Concat(flow.WrittenInside), found, visited);
            }
        }
    }

    private static ImmutableArray<ISymbol> Ordered(HashSet<ISymbol> found) =>
        found.OrderBy(symbol => IsThis(symbol) ? -1 : symbol.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0).ToImmutableArray();

    // The same tree as well as the span: a partial type's other file has offsets of its own.
    private static bool DeclaredWithin(ISymbol symbol, SyntaxNode scope) =>
        symbol.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == scope.SyntaxTree && scope.Span.Contains(reference.Span));

    private static (HashSet<ISymbol> Symbols, bool This, List<IMethodSymbol> LocalFunctions) Uses(IOperation body)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var usesThis = false;
        var localFunctions = new List<IMethodSymbol>();
        var pending = new Stack<IOperation>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            var operation = pending.Pop();
            switch (operation)
            {
                case INameOfOperation:
                    continue;
                case ILocalReferenceOperation local:
                    symbols.Add(local.Local);
                    break;
                case IParameterReferenceOperation parameter:
                    symbols.Add(parameter.Parameter);
                    break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                    usesThis = true;
                    break;
                case IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction } call:
                    localFunctions.Add(call.TargetMethod.OriginalDefinition);
                    break;
                case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference:
                    localFunctions.Add(reference.Method.OriginalDefinition);
                    break;
            }

            foreach (var child in operation.ChildOperations)
            {
                pending.Push(child);
            }
        }

        return (symbols, usesThis, localFunctions);
    }
}
