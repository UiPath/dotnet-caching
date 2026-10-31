using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UiPath.Caching.Analyzers;

/// <summary>Rewrites a generator argument into a state argument and a static lambda, or declines when that could change what the call does.</summary>
internal static class StateRewrite
{
    private static readonly ImmutableHashSet<string> TupleMemberNames =
        ImmutableHashSet.Create("CompareTo", "Equals", "GetHashCode", "GetType", "Rest", "ToString");

    private enum CaptureUse
    {
        Read,
        Write,
        Unsafe,
    }

    /// <summary>The call with the state inserted before <paramref name="argument"/> and the generator made static, or null when there is no safe rewrite.</summary>
    /// <param name="stateTypeIsLast">Whether the overload's state type is its last type parameter.</param>
    /// <param name="verify">Whether to compile the rewritten call in place first; the code fix defers that to when it is applied.</param>
    public static Rewritten? Rewrite(SemanticModel model, ArgumentSyntax argument, string stateParameter, bool stateTypeIsLast, CancellationToken cancellationToken, bool verify = true)
    {
        if (argument.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp9 }
            || argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } list
            || IsStaticFormExtensionCall(model, invocation, cancellationToken)
            || IsInExpressionTree(model, invocation, cancellationToken)
            || model.GetOperation(argument, cancellationToken) is not IArgumentOperation { Value: IDelegateCreationOperation { Target: var target } })
        {
            return null;
        }

        // Two captures go to the owner and argument overload when the call has one, else to a tuple.
        if (target is IAnonymousFunctionOperation pair
            && FromLambda(pair, invocation, ownerArgument: true, stateParameter) is { } ownerArgument
            && Build(model, invocation, list, argument, ownerArgument, true, verify, cancellationToken) is { } overload)
        {
            return overload;
        }

        var shape = target switch
        {
            IAnonymousFunctionOperation lambda => FromLambda(lambda, invocation, ownerArgument: false, stateParameter),
            IMethodReferenceOperation group => FromMethodGroup(group, argument.Expression, stateParameter),
            _ => null,
        };
        return shape is null ? null : Build(model, invocation, list, argument, shape, stateTypeIsLast, verify, cancellationToken);
    }

    internal static bool TakesState(IMethodSymbol method, string stateParameter) =>
        (method.ReducedFrom ?? method.OriginalDefinition).Parameters.Any(parameter => parameter.Name == stateParameter && parameter.Type.TypeKind == TypeKind.TypeParameter);

    private static Rewritten? Build(SemanticModel model, InvocationExpressionSyntax invocation, ArgumentListSyntax list, ArgumentSyntax argument, Shape shape, bool stateTypeIsLast, bool verify, CancellationToken cancellationToken)
    {
        // A dynamic state would bind the call at run time, where a lambda cannot be an argument (CS1977).
        if (shape.StateTypes.Any(type => type is null || Contains(type, candidate => candidate.TypeKind == TypeKind.Dynamic))
            || WithStateTypeArguments(invocation.Expression, shape.StateTypes!, stateTypeIsLast, model, invocation.SpanStart) is not { } callee)
        {
            return null;
        }

        var call = invocation.WithExpression(callee).WithArgumentList(InsertState(list, argument, shape));
        var rewritten = new Rewritten(invocation, call, shape.ArgumentNames[0], shape.Snapshots);
        // The owner and argument overload may not exist for the call, and a fallback needs to know; a snapshot local or a conditional call (`cache?.X(...)`) cannot be bound apart from its place.
        var inPlaceOnly = !shape.Snapshots.IsEmpty || invocation.Expression is MemberBindingExpressionSyntax;
        verify |= shape.OwnerArgument && inPlaceOnly;
        return (inPlaceOnly || BindsToStateOverload(model, invocation, call, shape.ArgumentNames[0], cancellationToken)) && (!verify || rewritten.Compiles(model, cancellationToken)) ? rewritten : null;
    }

    /// <summary>Whether the rewritten call, bound speculatively where the original stands, resolves to an overload that takes <paramref name="stateParameter"/>: a cheap check that needs no new compilation.</summary>
    private static bool BindsToStateOverload(SemanticModel model, InvocationExpressionSyntax original, InvocationExpressionSyntax rewritten, string stateParameter, CancellationToken cancellationToken) =>
        model.GetSpeculativeSymbolInfo(original.SpanStart, rewritten, SpeculativeBindingOption.BindAsExpression).Symbol is IMethodSymbol method
        && TakesState(method, stateParameter)
        && !cancellationToken.IsCancellationRequested;

    // An expression tree takes neither a static lambda nor a tuple literal (CS8143).
    private static bool IsInExpressionTree(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken) =>
        invocation.Ancestors().OfType<LambdaExpressionSyntax>().Any(lambda =>
            model.GetTypeInfo(lambda, cancellationToken).ConvertedType is INamedTypeSymbol { Name: "Expression", ContainingNamespace: { Name: "Expressions", ContainingNamespace: { Name: "Linq", ContainingNamespace.Name: "System" } } });

    // `CacheExtensions.GetOrAddAsync(cache, ...)` names the class, and the state overload may live in another one.
    private static bool IsStaticFormExtensionCall(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol { IsExtensionMethod: true, ReducedFrom: null };

    /// <summary>The callee with the state types appended to its spelled type arguments, unchanged without them, or null when they cannot be spelled there.</summary>
    private static ExpressionSyntax? WithStateTypeArguments(ExpressionSyntax callee, IEnumerable<ITypeSymbol> stateTypes, bool stateTypeIsLast, SemanticModel model, int position)
    {
        var name = callee switch
        {
            MemberAccessExpressionSyntax access => access.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            SimpleNameSyntax simple => simple,
            _ => null,
        };
        if (name is not GenericNameSyntax generic)
        {
            return callee;
        }

        if (!stateTypeIsLast || stateTypes.Any(stateType => Contains(stateType, type => type.IsAnonymousType || type.TypeKind == TypeKind.Error)))
        {
            return null;
        }

        var widened = generic.WithTypeArgumentList(generic.TypeArgumentList.AddArguments(stateTypes.Select(stateType => SyntaxFactory.ParseTypeName(stateType.ToMinimalDisplayString(model, position))).ToArray()));
        return callee switch
        {
            MemberAccessExpressionSyntax access => access.WithName(widened),
            MemberBindingExpressionSyntax binding => binding.WithName(widened),
            _ => widened,
        };
    }

    private static ArgumentListSyntax InsertState(ArgumentListSyntax list, ArgumentSyntax argument, Shape shape)
    {
        var index = list.Arguments.IndexOf(argument);
        var generator = shape.Generator;
        var stateArguments = shape.States.Select((state, i) =>
        {
            var stateArgument = SyntaxFactory.Argument(state);
            return argument.NameColon is not null
                ? stateArgument.WithNameColon(SyntaxFactory.NameColon(shape.ArgumentNames[i]).WithTrailingTrivia(SyntaxFactory.Space))
                : stateArgument;
        }).ToList();
        stateArguments[0] = stateArguments[0].WithLeadingTrivia(argument.GetLeadingTrivia());

        // The comma after the state copies the layout of the one before the generator, so arguments on their own lines stay
        // that way; a comment there stays with the state it now leads.
        var layout = index > 0
            ? SyntaxFactory.TriviaList(list.Arguments.GetSeparator(index - 1).TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
            : default;
        var comma = SyntaxFactory.Token(SyntaxKind.CommaToken)
            .WithTrailingTrivia(layout.Count > 0 ? layout : SyntaxFactory.TriviaList(SyntaxFactory.Space));
        var indentation = SyntaxFactory.TriviaList(argument.GetLeadingTrivia().Reverse().TakeWhile(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse());
        // What led the generator now leads the state; the generator keeps only its indentation, so a comment is not doubled.
        var inserted = stateArguments.Skip(1).Select(state => state.WithLeadingTrivia(indentation)).Aggregate(
            new List<SyntaxNodeOrToken> { stateArguments[0], comma },
            (nodes, state) => { nodes.Add(state); nodes.Add(comma); return nodes; });
        var nodesAndTokens = list.Arguments.GetWithSeparators()
            .Replace(argument, argument.WithExpression(generator.WithTriviaFrom(argument.Expression)).WithLeadingTrivia(indentation))
            .InsertRange(index * 2, inserted);
        return list.WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens));
    }

    private static Shape? FromLambda(IAnonymousFunctionOperation lambda, InvocationExpressionSyntax invocation, bool ownerArgument, string stateParameter)
    {
        var syntax = (AnonymousFunctionExpressionSyntax)lambda.Syntax;
        var captured = GeneratorCaptures.Of(lambda);
        if (!CanMove(lambda, syntax, captured) || (ownerArgument && captured.Length != 2) || Reassigned(lambda, captured, invocation) is not { } reassigned)
        {
            return null;
        }

        var usedNames = syntax.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText).ToImmutableHashSet();
        var snapshots = SnapshotNames(reassigned, invocation);
        var (states, parameters, access, rewritten) = StateShape(captured, usedNames, DeclaredNames(syntax), symbol => snapshots.TryGetValue(symbol, out var name) ? name : symbol.Name, ownerArgument);
        if (Replacements(lambda, captured, access, rewritten) is not { } replacements)
        {
            return null;
        }

        var text = StaticLambdaText(lambda, syntax, parameters, usedNames, replacements);
        var types = ownerArgument || captured.Length == 1 ? captured.Select(TypeOf).ToImmutableArray() : ImmutableArray.Create(TupleOf(lambda.SemanticModel!, captured, ElementNames(captured)));
        var declarations = snapshots.Select(pair => (LocalDeclarationStatementSyntax)SyntaxFactory.ParseStatement($"var {pair.Value} = {Name(pair.Key.Name).ToString()};")).ToImmutableArray();
        return new Shape(states, ownerArgument ? ImmutableArray.Create("owner", "argument") : ImmutableArray.Create(stateParameter), SyntaxFactory.ParseExpression(text), types, declarations, ownerArgument);
    }

    private static bool CanMove(IAnonymousFunctionOperation lambda, AnonymousFunctionExpressionSyntax syntax, ImmutableArray<ISymbol> captured) =>
        !captured.IsEmpty
        && !syntax.DescendantNodes().OfType<BaseExpressionSyntax>().Any()
        && syntax is not ParenthesizedLambdaExpressionSyntax { AttributeLists.Count: > 0 }
        && captured.All(CanPass)
        && !GeneratorCaptures.UsesOuterLocalFunction(lambda)
        && !HasImplicitCallerArgumentExpression(lambda)
        && !captured.Any(symbol => GeneratorCaptures.IsPrimaryConstructorParameter(symbol) && (IsAssignedInType((IParameterSymbol)symbol) || IsMutableStruct(((IParameterSymbol)symbol).Type)));

    /// <summary>The captures written before the call, which need a snapshot, or null when one is written where a snapshot would not hold: after the call, inside the lambda or another function, or in a loop around the call.</summary>
    private static ImmutableArray<ISymbol>? Reassigned(IAnonymousFunctionOperation lambda, ImmutableArray<ISymbol> captured, InvocationExpressionSyntax invocation)
    {
        var root = RootOf(lambda);
        var statement = invocation.FirstAncestorOrSelf<StatementSyntax>();
        var function = FunctionOf(invocation);
        var written = new List<ISymbol>();
        foreach (var operation in root.DescendantsAndSelf())
        {
            if (Referenced(operation) is not { } symbol || !captured.Contains(symbol, SymbolEqualityComparer.Default))
            {
                continue;
            }

            var use = UseOf(operation, statement, function);
            if (use == CaptureUse.Unsafe)
            {
                return null;
            }

            if (use == CaptureUse.Write && !written.Contains(symbol, SymbolEqualityComparer.Default))
            {
                written.Add(symbol);
            }
        }

        return written.Count == 0 || CanSnapshot(written, statement, root, invocation) ? written.ToImmutableArray() : null;
    }

    // A write a snapshot cannot hold: through a member or an alias (which writes whenever it likes), or one not settled before the call.
    private static CaptureUse UseOf(IOperation operation, StatementSyntax? statement, SyntaxNode? function)
    {
        if (IsMutatedThrough(operation) || IsByRef(operation))
        {
            return CaptureUse.Unsafe;
        }

        if (!IsWrite(operation))
        {
            return CaptureUse.Read;
        }

        return IsSettledBefore(operation.Syntax, statement, function) ? CaptureUse.Write : CaptureUse.Unsafe;
    }

    private static IOperation RootOf(IOperation operation)
    {
        while (operation.Parent is not null)
        {
            operation = operation.Parent;
        }

        return operation;
    }

    // A write holds for a snapshot when it runs in an earlier statement of the same function, and no loop around the call runs it again.
    private static bool IsSettledBefore(SyntaxNode write, StatementSyntax? statement, SyntaxNode? function) =>
        statement is not null
        && write.Span.End <= statement.SpanStart
        && FunctionOf(write) == function
        && !statement.Ancestors().Any(ancestor => ancestor is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax && ancestor.Span.Contains(write.Span));

    // The snapshot is a statement of its own just before the call's, with the variable declared before it; a jump back could run the writes again.
    private static bool CanSnapshot(List<ISymbol> written, StatementSyntax? statement, IOperation root, InvocationExpressionSyntax invocation) =>
        statement is { Parent: BlockSyntax or SwitchSectionSyntax }
        && written.All(symbol => symbol.DeclaringSyntaxReferences.All(reference => reference.SyntaxTree == statement.SyntaxTree && reference.Span.End <= statement.SpanStart))
        && !root.DescendantsAndSelf().Any(operation => operation is ILabeledOperation)
        && !HasGotoCase(invocation)
        && !IsConditionallyEvaluated(invocation, statement);

    // `goto case` and `goto default` jump back to an earlier section like a label does.
    private static bool HasGotoCase(InvocationExpressionSyntax invocation) =>
        invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.DescendantNodes().OfType<GotoStatementSyntax>()
            .Any(jump => jump.IsKind(SyntaxKind.GotoCaseStatement) || jump.IsKind(SyntaxKind.GotoDefaultStatement)) == true;

    // A snapshot before the statement would lose the null state a condition earlier in it establishes.
    private static bool IsConditionallyEvaluated(InvocationExpressionSyntax invocation, StatementSyntax statement)
    {
        SyntaxNode child = invocation;
        foreach (var ancestor in invocation.Ancestors().TakeWhile(node => node != statement))
        {
            var conditional = ancestor switch
            {
                BinaryExpressionSyntax binary => (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.CoalesceExpression)) && binary.Right == child,
                AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) && assignment.Right == child,
                ConditionalExpressionSyntax ternary => ternary.Condition != child,
                ConditionalAccessExpressionSyntax access => access.WhenNotNull == child,
                SwitchExpressionArmSyntax or WhenClauseSyntax => true,
                _ => false,
            };
            if (conditional)
            {
                return true;
            }

            child = ancestor;
        }

        return false;
    }

    // A `ref` expression, or a `ref` or `in` argument, may let the variable be written through an alias.
    private static bool IsByRef(IOperation reference) =>
        reference.Syntax.Parent is RefExpressionSyntax
        || reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.In };

    private static SyntaxNode? FunctionOf(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or (MemberDeclarationSyntax and not GlobalStatementSyntax));

    // `<name>0`, counting up past any name the member already uses. Fix All applies every call's fix to the original tree, so call i of the member's N
    // lambda-taking calls, whatever their method, only uses suffixes i, i + N, i + 2N...: two snapshots of one variable then differ.
    private static Dictionary<ISymbol, string> SnapshotNames(ImmutableArray<ISymbol> reassigned, InvocationExpressionSyntax invocation)
    {
        var names = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        if (reassigned.IsEmpty)
        {
            return names;
        }

        var used = (invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? (SyntaxNode)invocation).DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText).ToImmutableHashSet();
        var calls = (invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.DescendantNodes().OfType<InvocationExpressionSyntax>() ?? Enumerable.Empty<InvocationExpressionSyntax>())
            .Where(other => other.ArgumentList.Arguments.Any(argument => argument.Expression is AnonymousFunctionExpressionSyntax)).ToList();
        var step = Math.Max(calls.Count, 1);
        var first = calls.Count(other => other.SpanStart < invocation.SpanStart);
        foreach (var symbol in reassigned)
        {
            var suffix = first;
            while (used.Contains(symbol.Name + suffix))
            {
                suffix += step;
            }

            names[symbol] = symbol.Name + suffix;
            used = used.Add(names[symbol]);
        }

        return names;
    }

    // A rewritten reference would change the text an omitted [CallerArgumentExpression] argument records, such as ThrowIfNull's ParamName.
    private static bool HasImplicitCallerArgumentExpression(IAnonymousFunctionOperation lambda) =>
        lambda.Body.DescendantsAndSelf().OfType<IArgumentOperation>().Any(argument =>
            argument.ArgumentKind == ArgumentKind.DefaultValue
            && argument.Parameter?.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "CallerArgumentExpressionAttribute") == true);

    /// <summary>The state arguments, the lambda parameters that receive them, how the body reaches each captured value through them, and the captures whose references change.</summary>
    /// <param name="stateName">What names a capture in the state argument: the variable, or its snapshot.</param>
    /// <param name="ownerArgument">Whether there are two captures, each its own state, for the owner and argument overload.</param>
    private static (ImmutableArray<ExpressionSyntax> States, ImmutableArray<string> Parameters, Func<ISymbol, ExpressionSyntax> Access, ImmutableArray<ISymbol> Rewritten) StateShape(
        ImmutableArray<ISymbol> captured, ImmutableHashSet<string> usedNames, ImmutableHashSet<string> declaredNames, Func<ISymbol, string> stateName, bool ownerArgument)
    {
        if (ownerArgument || captured.Length == 1)
        {
            return OwnStateShape(captured, usedNames, declaredNames, stateName);
        }

        var tuple = Unique("s", usedNames);
        var elements = ElementNames(captured);
        var state = SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(captured.Select(symbol =>
            SyntaxFactory.Argument(SyntaxFactory.NameColon(elements[symbol]), default, GeneratorCaptures.IsThis(symbol) ? SyntaxFactory.ThisExpression() : Name(stateName(symbol))))));
        return (ImmutableArray.Create<ExpressionSyntax>(state), ImmutableArray.Create(tuple), symbol => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Name(tuple), Name(elements[symbol])), captured);
    }

    // Each value is its own state.
    private static (ImmutableArray<ExpressionSyntax> States, ImmutableArray<string> Parameters, Func<ISymbol, ExpressionSyntax> Access, ImmutableArray<ISymbol> Rewritten) OwnStateShape(
        ImmutableArray<ISymbol> captured, ImmutableHashSet<string> usedNames, ImmutableHashSet<string> declaredNames, Func<ISymbol, string> stateName)
    {
        var used = usedNames;
        var parameters = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var symbol in captured)
        {
            var parameter = ParameterFor(symbol, used, declaredNames);
            parameters[symbol] = parameter;
            used = used.Add(parameter);
        }

        return (
            captured.Select(symbol => GeneratorCaptures.IsThis(symbol) ? (ExpressionSyntax)SyntaxFactory.ThisExpression() : Name(stateName(symbol))).ToImmutableArray(),
            captured.Select(symbol => parameters[symbol]).ToImmutableArray(),
            symbol => Name(parameters[symbol]),
            captured.Where(symbol => !GeneratorCaptures.IsThis(symbol) && parameters[symbol] != symbol.Name).ToImmutableArray());
    }

    // A local keeps its name, as a lambda parameter may shadow it (C# 8+), unless the body declares it again.
    private static string ParameterFor(ISymbol symbol, ImmutableHashSet<string> used, ImmutableHashSet<string> declaredNames)
    {
        if (GeneratorCaptures.IsThis(symbol))
        {
            return Unique("self", used);
        }

        return declaredNames.Contains(symbol.Name) ? Unique(symbol.Name, used) : symbol.Name;
    }

    // The names the lambda declares inside itself: locals, pattern and out variables, parameters of nested functions, query variables.
    private static ImmutableHashSet<string> DeclaredNames(SyntaxNode lambda) =>
        lambda.DescendantNodes().Select(node => node switch
        {
            VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
            SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
            ParameterSyntax parameter => parameter.Identifier.ValueText,
            ForEachStatementSyntax loop => loop.Identifier.ValueText,
            CatchDeclarationSyntax exception => exception.Identifier.ValueText,
            LocalFunctionStatementSyntax function => function.Identifier.ValueText,
            FromClauseSyntax from => from.Identifier.ValueText,
            LetClauseSyntax let => let.Identifier.ValueText,
            JoinClauseSyntax join => join.Identifier.ValueText,
            JoinIntoClauseSyntax into => into.Identifier.ValueText,
            QueryContinuationSyntax continuation => continuation.Identifier.ValueText,
            _ => null,
        }).Where(name => name is not null).Select(name => name!).ToImmutableHashSet();

    /// <summary>The body's references to the captured values, each mapped to its access through the state, or null when one cannot be rewritten.</summary>
    private static Dictionary<SyntaxNode, ExpressionSyntax>? Replacements(IAnonymousFunctionOperation lambda, ImmutableArray<ISymbol> captured, Func<ISymbol, ExpressionSyntax> access, ImmutableArray<ISymbol> rewritten)
    {
        var self = captured.FirstOrDefault(GeneratorCaptures.IsThis);
        var replacements = new Dictionary<SyntaxNode, ExpressionSyntax>();
        return lambda.Body.DescendantsAndSelf().All(operation => TryReplace(operation, self, rewritten, access, replacements)) ? replacements : null;
    }

    /// <summary>Records the rewrite of one reference: through `self` for an explicit or implicit `this`, through the state for a capture in <paramref name="rewritten"/>.</summary>
    private static bool TryReplace(IOperation operation, ISymbol? self, ImmutableArray<ISymbol> rewritten, Func<ISymbol, ExpressionSyntax> access, Dictionary<SyntaxNode, ExpressionSyntax> replacements)
    {
        switch (operation)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, IsImplicit: false } when self is not null:
                replacements[operation.Syntax] = access(self);
                return true;
            case IMemberReferenceOperation { Instance: { } instance } when self is not null && IsImplicitThis(instance):
                return TryQualify(operation.Syntax as SimpleNameSyntax, access(self), replacements);
            case IInvocationOperation { Instance: { } instance } when self is not null && IsImplicitThis(instance):
                return TryQualify((operation.Syntax as InvocationExpressionSyntax)?.Expression as SimpleNameSyntax, access(self), replacements);
            case IDynamicMemberReferenceOperation { Instance: { } instance } when self is not null && IsImplicitThis(instance):
                return TryQualify(operation.Syntax as SimpleNameSyntax, access(self), replacements);
            case ILocalReferenceOperation or IParameterReferenceOperation when Referenced(operation) is { } symbol && rewritten.Contains(symbol, SymbolEqualityComparer.Default):
                replacements[operation.Syntax] = access(symbol);
                return !IsNamedByInference(operation.Syntax);
            default:
                return true;
        }
    }

    private static bool TryQualify(SimpleNameSyntax? member, ExpressionSyntax receiver, Dictionary<SyntaxNode, ExpressionSyntax> replacements)
    {
        if (member is null)
        {
            return false;
        }

        replacements[member] = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, member.WithoutTrivia());
        return true;
    }

    // s.Id where `id` named something by inference would rename it.
    private static bool IsNamedByInference(SyntaxNode reference) =>
        reference.Parent is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null } or ArgumentSyntax { NameColon: null, Parent: TupleExpressionSyntax }
        || reference.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private static string StaticLambdaText(IAnonymousFunctionOperation lambda, AnonymousFunctionExpressionSyntax syntax, ImmutableArray<string> stateParameters, ImmutableHashSet<string> usedNames, Dictionary<SyntaxNode, ExpressionSyntax> replacements)
    {
        var body = syntax.Body.ReplaceNodes(replacements.Keys, (original, _) => replacements[original].WithTriviaFrom(original));
        var returnType = syntax is ParenthesizedLambdaExpressionSyntax { ReturnType: { } type } ? type.ToString() + " " : string.Empty;
        // The body keeps its own layout, and the line break after the arrow when there was one.
        var bodyText = (syntax is LambdaExpressionSyntax lambdaSyntax ? lambdaSyntax.ArrowToken.TrailingTrivia.ToFullString() : string.Empty) + body.ToFullString();
        var parameters = string.Join(", ", LambdaParameters(lambda, syntax, stateParameters, usedNames));
        return $"static {(lambda.Symbol.IsAsync ? "async " : string.Empty)}{returnType}({parameters}) =>{(bodyText.Length > 0 && char.IsWhiteSpace(bodyText[0]) ? string.Empty : " ")}{bodyText}";
    }

    // The state first, then the lambda's own parameters; an anonymous method without a list gets a name for each, none repeated.
    private static IEnumerable<string> LambdaParameters(IAnonymousFunctionOperation lambda, AnonymousFunctionExpressionSyntax syntax, ImmutableArray<string> stateParameters, ImmutableHashSet<string> usedNames)
    {
        var used = usedNames.Union(stateParameters);
        var names = new List<string>(stateParameters);
        for (var i = 0; i < lambda.Symbol.Parameters.Length; i++)
        {
            var name = ParameterName(syntax, i) ?? Unique(lambda.Symbol.Parameters[i].Type.Name == "CancellationToken" ? "ct" : "arg", used);
            used = used.Add(name);
            names.Add(name);
        }

        return names.Select(Escape);
    }

    private static Shape? FromMethodGroup(IMethodReferenceOperation group, ExpressionSyntax syntax, string stateParameter)
    {
        if (group.Method.MethodKind == MethodKind.LocalFunction
            || group.Instance is not { Type.IsValueType: false } instance
            || (group.Method.IsGenericMethod && syntax is not (GenericNameSyntax or MemberAccessExpressionSyntax { Name: GenericNameSyntax }))
            || MayBeNull(group, instance, syntax)
            || Target(syntax, instance) is not var (state, method, parameter))
        {
            return null;
        }

        // The lambda's parameters take the method's names: (self, ct) for a single key, (self, ids, ct) for a batch.
        var used = ImmutableHashSet.Create(parameter);
        var names = new List<string>();
        // An extension method converted on its receiver takes that receiver as its first parameter.
        var methodParameters = group.Method is { IsExtensionMethod: true, ReducedFrom: null } ? group.Method.Parameters.Skip(1) : group.Method.Parameters;
        foreach (var methodParameter in methodParameters)
        {
            var name = Unique(methodParameter.Type.Name == "CancellationToken" ? "ct" : methodParameter.Name, used);
            names.Add(name);
            used = used.Add(name);
        }

        var arguments = string.Join(", ", names.Select(Escape));
        var text = $"static ({string.Join(", ", names.Prepend(parameter).Select(Escape))}) => {Escape(parameter)}.{method.WithoutTrivia().ToFullString()}({arguments})";
        return new Shape(ImmutableArray.Create(state), ImmutableArray.Create(stateParameter), SyntaxFactory.ParseExpression(text), ImmutableArray.Create<ITypeSymbol?>(instance.Type), ImmutableArray<LocalDeclarationStatementSyntax>.Empty, false);
    }

    /// <summary>The method group's target as the state, the method's name, and the lambda parameter for the target.</summary>
    private static (ExpressionSyntax State, SimpleNameSyntax Method, string Parameter)? Target(ExpressionSyntax syntax, IOperation instance)
    {
        switch (syntax)
        {
            case SimpleNameSyntax name when IsImplicitThis(instance):
                return (SyntaxFactory.ThisExpression(), name, "self");
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access:
                return (SyntaxFactory.ThisExpression(), access.Name, "self");
            case MemberAccessExpressionSyntax { Expression: not BaseExpressionSyntax } access:
                var receiver = access.Expression is SimpleNameSyntax simple ? simple.Identifier.ValueText.TrimStart('_') : string.Empty;
                var camel = receiver.Length == 0 || !SyntaxFacts.IsValidIdentifier(receiver) ? "target" : char.ToLowerInvariant(receiver[0]) + receiver.Substring(1);
                return (access.Expression.WithoutTrivia(), access.Name, camel);
            default:
                return null;
        }
    }

    // Converting `repo.LoadAsync` throws there and then on a null `repo`; as state, a null would surface only on a miss. An extension method takes a null receiver either way.
    private static bool MayBeNull(IMethodReferenceOperation group, IOperation instance, ExpressionSyntax syntax)
    {
        if (group.Method.IsExtensionMethod || group.Method.ReducedFrom is not null || instance is IInstanceReferenceOperation)
        {
            return false;
        }

        var receiver = syntax is MemberAccessExpressionSyntax access ? access.Expression : instance.Syntax;
        while (receiver is ParenthesizedExpressionSyntax parenthesized)
        {
            receiver = parenthesized.Expression;
        }

        return receiver is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression }
            || group.SemanticModel?.GetTypeInfo(receiver).Nullability.FlowState != NullableFlowState.NotNull;
    }

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        IParameterSymbol parameter => parameter.Type,
        ILocalSymbol local => local.Type,
        _ => null,
    };

    private static ITypeSymbol? TupleOf(SemanticModel model, ImmutableArray<ISymbol> captured, Dictionary<ISymbol, string> names)
    {
        var types = captured.Select(TypeOf).ToList();
        return types.Any(type => type is null)
            ? null
            : model.Compilation.CreateTupleTypeSymbol(types.Select(type => type!).ToImmutableArray(), captured.Select(symbol => (string?)names[symbol]).ToImmutableArray());
    }

    // Whether the type, or any type in it (an element, a type argument, an outer type), matches.
    private static bool Contains(ITypeSymbol type, Func<ITypeSymbol, bool> match) =>
        match(type) || type switch
        {
            IArrayTypeSymbol array => Contains(array.ElementType, match),
            IPointerTypeSymbol pointer => Contains(pointer.PointedAtType, match),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => Contains(argument, match)) || (named.ContainingType is { } outer && Contains(outer, match)),
            _ => false,
        };

    // A value passed as state is read once, at the call, where the lambda read it when it ran: the same only if nothing writes it.
    // A pointer cannot be a type argument, alone or in a tuple.
    private static bool CanPass(ISymbol symbol) => symbol switch
    {
        IParameterSymbol { IsThis: true } self => self.Type.IsReferenceType,
        IParameterSymbol parameter => parameter.RefKind == RefKind.None && !parameter.Type.IsRefLikeType && !IsPointer(parameter.Type),
        ILocalSymbol local => !local.IsRef && !local.Type.IsRefLikeType && !IsPointer(local.Type),
        _ => false,
    };

    private static bool IsPointer(ITypeSymbol type) =>
        Contains(type, candidate => candidate.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer);

    // Any member may assign a primary constructor parameter; a syntactic look over the type is enough to stay away from those.
    private static bool IsAssignedInType(IParameterSymbol parameter)
    {
        bool Named(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText == parameter.Name,
            ParenthesizedExpressionSyntax parenthesized => Named(parenthesized.Expression),
            TupleExpressionSyntax tuple => tuple.Arguments.Any(argument => Named(argument.Expression)),
            _ => false,
        };

        return parameter.ContainingType.DeclaringSyntaxReferences.SelectMany(reference => reference.GetSyntax().DescendantNodes()).Any(node => node switch
        {
            AssignmentExpressionSyntax assignment => Named(assignment.Left),
            PrefixUnaryExpressionSyntax unary => (unary.IsKind(SyntaxKind.PreIncrementExpression) || unary.IsKind(SyntaxKind.PreDecrementExpression)) && Named(unary.Operand),
            PostfixUnaryExpressionSyntax unary => (unary.IsKind(SyntaxKind.PostIncrementExpression) || unary.IsKind(SyntaxKind.PostDecrementExpression)) && Named(unary.Operand),
            ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword } argument => Named(argument.Expression),
            RefExpressionSyntax reference => Named(reference.Expression),
            _ => false,
        });
    }

    private static bool IsWrite(IOperation reference)
    {
        var operation = reference;
        while (operation.Parent is ITupleOperation)
        {
            operation = operation.Parent;
        }

        return operation.Parent switch
        {
            IAssignmentOperation assignment => assignment.Target == operation,
            IIncrementOrDecrementOperation => true,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => true,
            _ => reference.Syntax.Parent is RefExpressionSyntax,
        };
    }

    // A copy in the state would miss a field write, or a mutating method call, on a mutable struct the lambda reads.
    private static bool IsMutatedThrough(IOperation reference)
    {
        if (!IsMutableStruct(reference.Type))
        {
            return false;
        }

        var current = reference;
        while (current.Parent is IFieldReferenceOperation field && field.Instance == current)
        {
            current = field;
        }

        return current.Parent switch
        {
            IPropertyReferenceOperation property when property.Instance == current => IsWrite(property) || property.Property.GetMethod is { IsReadOnly: false },
            IInvocationOperation call when call.Instance == current => !call.TargetMethod.IsReadOnly,
            _ => current != reference && IsWrite(current),
        };
    }

    // A type parameter not known to be a reference type may be a mutable struct.
    private static bool IsMutableStruct(ITypeSymbol? type) =>
        type is ITypeParameterSymbol { IsReferenceType: false }
        || (type is { IsValueType: true, IsReadOnly: false, TypeKind: TypeKind.Struct, SpecialType: SpecialType.None }
            && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T);

    private static ISymbol? Referenced(IOperation operation) => operation switch
    {
        ILocalReferenceOperation { IsDeclaration: false } local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        _ => null,
    };

    private static bool IsImplicitThis(IOperation instance) =>
        instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, IsImplicit: true };

    private static Dictionary<ISymbol, string> ElementNames(ImmutableArray<ISymbol> captured)
    {
        var names = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var used = ImmutableHashSet<string>.Empty;
        foreach (var symbol in captured)
        {
            var bare = GeneratorCaptures.IsThis(symbol) ? "Self" : symbol.Name.TrimStart('_');
            var pascal = bare.Length == 0 || !SyntaxFacts.IsValidIdentifier(bare) ? "Value" + bare : char.ToUpperInvariant(bare[0]) + bare.Substring(1);
            var name = TupleMemberNames.Contains(pascal) || (pascal.Length > 4 && pascal.StartsWith("Item", StringComparison.Ordinal) && pascal.Substring(4).All(char.IsDigit))
                ? pascal + "Value"
                : pascal;
            name = Unique(name, used);
            used = used.Add(name);
            names[symbol] = name;
        }

        return names;
    }

    private static string? ParameterName(AnonymousFunctionExpressionSyntax syntax, int index) => syntax switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters[index].Identifier.ValueText,
        AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters[index].Identifier.ValueText,
        _ => null,
    };

    private static string Unique(string name, ImmutableHashSet<string> used)
    {
        var candidate = name;
        var suffix = 2;
        while (used.Contains(candidate))
        {
            candidate = name + suffix++;
        }

        return candidate;
    }

    // A lone underscore is a discard in a lambda's parameter list, not a keyword to escape.
    private static string Escape(string name) =>
        name == "_" || (SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None) ? name : "@" + name;

    // A keyword name needs a verbatim token: `@await` as its text, `await` as the name it binds to.
    private static IdentifierNameSyntax Name(string name) =>
        Escape(name) == name ? SyntaxFactory.IdentifierName(name) : (IdentifierNameSyntax)SyntaxFactory.ParseName("@" + name);

    /// <summary>What replaces the generator: the state arguments (named <c>ArgumentNames</c> in the overload), the static lambda, the state types, and the locals that snapshot reassigned captures.</summary>
    private sealed class Shape(
        ImmutableArray<ExpressionSyntax> states,
        ImmutableArray<string> argumentNames,
        ExpressionSyntax generator,
        ImmutableArray<ITypeSymbol?> stateTypes,
        ImmutableArray<LocalDeclarationStatementSyntax> snapshots,
        bool ownerArgument)
    {
        public ImmutableArray<ExpressionSyntax> States { get; } = states;

        public ImmutableArray<string> ArgumentNames { get; } = argumentNames;

        public ExpressionSyntax Generator { get; } = generator;

        public ImmutableArray<ITypeSymbol?> StateTypes { get; } = stateTypes;

        public ImmutableArray<LocalDeclarationStatementSyntax> Snapshots { get; } = snapshots;

        public bool OwnerArgument { get; } = ownerArgument;
    }
}
