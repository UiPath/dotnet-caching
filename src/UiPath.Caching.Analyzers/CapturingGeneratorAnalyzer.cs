using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace UiPath.Caching.Analyzers;

/// <summary>UIPCACHE001: a generator passed to a UiPath.Caching <c>GetOrAddAsync</c> that allocates on every call, where a state overload would not.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapturingGeneratorAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The rule id.</summary>
    public const string DiagnosticId = "UIPCACHE001";

    /// <summary>The diagnostic property naming the state parameter of the overload the code fix moves the call to; absent when there is none.</summary>
    internal const string StateParameterProperty = "StateParameter";

    /// <summary>Present when the state's type is the overload's last type parameter, so a call that spells its type arguments can add it at the end.</summary>
    internal const string StateTypeIsLastProperty = "StateTypeIsLast";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Generator allocates on every call",
        messageFormat: "The generator {0} on every call, hits included; {1}",
        category: "UiPath.Caching",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A lambda that captures, or an instance method group, allocates a delegate (and a closure for captured locals) each time the call runs, including when the value is already cached. The state overloads take the values as an argument and a static lambda, which allocates nothing. The plain lambda is the documented default; raise this rule's severity where a profiler shows the allocation matters.",
        helpLinkUri: "https://github.com/UiPath/dotnet-caching/blob/main/docs/how-to/choosing-a-get-or-add-overload.md#the-uipcache001-suggestion");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (CacheMethods.Create(start.Compilation) is { } methods)
            {
                start.RegisterOperationAction(operation => AnalyzeInvocation(operation, methods), OperationKind.Invocation);
            }
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, CacheMethods methods)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!methods.IsCacheMethod(invocation.TargetMethod))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is { } parameter && argument.Value is IDelegateCreationOperation { Target: var target } && methods.IsGenerator(parameter.Type))
            {
                AnalyzeGenerator(context, invocation, methods, parameter, target, argument.Value.Syntax);
            }
        }
    }

    private static void AnalyzeGenerator(OperationAnalysisContext context, IInvocationOperation invocation, CacheMethods methods, IParameterSymbol parameter, IOperation target, SyntaxNode syntax)
    {
        var definition = invocation.TargetMethod.OriginalDefinition;
        var stateful = CacheMethods.IsStateful(definition, parameter.Ordinal);
        var sibling = stateful ? null : methods.StateSibling(definition, parameter.Ordinal);
        if (!stateful && sibling is null)
        {
            return;
        }

        // The parameter the values go to: the state (or argument) of the overload the fix moves the call to, or of the call itself.
        var into = sibling?.ParameterName ?? definition.Parameters[parameter.Ordinal - 1].Name;
        if (Describe(target, stateful, into) is var (allocation, advice))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.GetLocation(), FixProperties(invocation, sibling), allocation, advice));
        }
    }

    private static (string Allocation, string Advice)? Describe(IOperation target, bool stateful, string into) => target switch
    {
        IAnonymousFunctionOperation { Symbol.IsStatic: false } lambda => DescribeLambda(GeneratorCaptures.Of(lambda), stateful, into),
        IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } localFunction => DescribeLocalFunction(localFunction, stateful, into),
        IMethodReferenceOperation { Instance: not null } group => (
            $"is the instance method group '{group.Method.Name}' and allocates a delegate",
            stateful ? $"add its target to the {into} and call it from a static lambda" : $"pass its target as {Article(into)} and call it from a static lambda"),
        _ => null,
    };

    private static (string Allocation, string Advice)? DescribeLambda(ImmutableArray<ISymbol> captured, bool stateful, string into)
    {
        if (captured.IsEmpty)
        {
            return null;
        }

        var them = captured.Length == 1 ? "it" : "them";
        return (
            $"captures {Join(captured)} and allocates {Allocated(captured)}",
            stateful ? $"add {them} to the {into} and make the lambda static" : $"pass {them} as {Article(into)} and make the lambda static");
    }

    // A local function converted to the generator brings its closure along; a static one, or one that captures nothing, does not.
    private static (string Allocation, string Advice)? DescribeLocalFunction(IMethodReferenceOperation localFunction, bool stateful, string into)
    {
        var captured = localFunction.Method.IsStatic ? ImmutableArray<ISymbol>.Empty : GeneratorCaptures.OfLocalFunction(localFunction);
        if (captured.IsEmpty)
        {
            return null;
        }

        var them = captured.Length == 1 ? "it" : "them";
        return (
            $"is the local function '{localFunction.Method.Name}', which captures {Join(captured)}, and allocates {Allocated(captured)}",
            stateful ? $"add {them} to the {into} and use a static lambda" : $"pass {them} as {Article(into)} to a static lambda");
    }

    // Only 'this' (a field, a primary constructor parameter) binds the delegate to the instance; a local adds a closure.
    private static string Allocated(ImmutableArray<ISymbol> captured) =>
        captured.All(symbol => GeneratorCaptures.IsThis(symbol) || GeneratorCaptures.IsPrimaryConstructorParameter(symbol)) ? "a delegate" : "a closure";

    // A class does not see the state members of the cache interfaces, so the fix needs one it can call.
    private static ImmutableDictionary<string, string?> FixProperties(IInvocationOperation invocation, Sibling? sibling)
    {
        if (sibling is null || (sibling.IsInterfaceMember && ReceiverType(invocation) is { TypeKind: TypeKind.Class or TypeKind.Struct }))
        {
            return ImmutableDictionary<string, string?>.Empty;
        }

        var properties = ImmutableDictionary<string, string?>.Empty.Add(StateParameterProperty, sibling.ParameterName);
        return sibling.StateTypeIsLast ? properties.Add(StateTypeIsLastProperty, "true") : properties;
    }

    private static string Article(string parameter) => parameter == "state" ? "state" : "the " + parameter;

    private static ITypeSymbol? ReceiverType(IInvocationOperation invocation)
    {
        if (invocation.Instance is not null || !invocation.TargetMethod.IsExtensionMethod || invocation.Arguments.Length == 0)
        {
            return invocation.Instance?.Type;
        }

        var receiver = invocation.Arguments[0].Value;
        return (receiver is IConversionOperation conversion ? conversion.Operand : receiver).Type;
    }

    private static string Join(ImmutableArray<ISymbol> captured)
    {
        var names = captured.Select(symbol => $"'{GeneratorCaptures.Describe(symbol)}'").ToList();
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[names.Count - 1]}";
    }
}
