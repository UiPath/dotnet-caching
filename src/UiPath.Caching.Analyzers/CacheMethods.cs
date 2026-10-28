using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace UiPath.Caching.Analyzers;

/// <summary>Recognizes the generator parameters of the UiPath.Caching cache members, and the state overloads beside them.</summary>
/// <remarks>Read from the referenced library, not from a list of signatures.</remarks>
internal sealed class CacheMethods
{
    private readonly INamespaceSymbol _namespace;
    private readonly ImmutableArray<INamedTypeSymbol> _cacheTypes;
    private readonly ImmutableHashSet<string> _generatorMethodNames;
    private readonly INamedTypeSymbol _cancellationToken;
    private readonly INamedTypeSymbol _task;
    private readonly INamedTypeSymbol? _valueTask;
    private readonly ConcurrentDictionary<ISymbol, bool> _cacheMethods = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<(ISymbol Method, int Ordinal), Sibling?> _siblings = new(GeneratorKeyComparer.Instance);

    private CacheMethods(ImmutableArray<INamedTypeSymbol> cacheTypes, INamedTypeSymbol cancellationToken, INamedTypeSymbol task, INamedTypeSymbol? valueTask)
    {
        _namespace = cacheTypes[0].ContainingNamespace;
        _cacheTypes = cacheTypes;
        _cancellationToken = cancellationToken;
        _task = task;
        _valueTask = valueTask;
        _generatorMethodNames = cacheTypes
            .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
            .Where(method => method.Parameters.Any(parameter => IsGenerator(parameter.Type)))
            .Select(method => method.Name)
            .ToImmutableHashSet();
    }

    public static CacheMethods? Create(Compilation compilation)
    {
        var cache = compilation.GetTypeByMetadataName("UiPath.Caching.ICache");
        var cancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        if (cache is null || cancellationToken is null || task is null)
        {
            return null;
        }

        var cacheTypes = new[] { "UiPath.Caching.ICache`1", "UiPath.Caching.IHashCache", "UiPath.Caching.IHashCache`1", "UiPath.Caching.ISpanKeyCache", "UiPath.Caching.ISpanKeyCache`1", "UiPath.Caching.ISpanKeyHashCache", "UiPath.Caching.ISpanKeyHashCache`1" }
            .Select(compilation.GetTypeByMetadataName)
            .Where(type => type is not null)
            .Select(type => type!)
            .Prepend(cache)
            .ToImmutableArray();
        return new CacheMethods(cacheTypes, cancellationToken, task, compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"));
    }

    /// <summary>Whether the generator at <paramref name="ordinal"/> already takes the state or argument passed right before it.</summary>
    public static bool IsStateful(IMethodSymbol definition, int ordinal) =>
        ordinal > 0
        && definition.Parameters[ordinal - 1].Type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } state
        && definition.Parameters[ordinal].Type is INamedTypeSymbol { TypeArguments.Length: > 0 } generator
        && SymbolEqualityComparer.Default.Equals(generator.TypeArguments[0], state);

    /// <summary>Whether <paramref name="method"/> is a cache member, an extension of one, or a class member that implements one, and may take a generator.</summary>
    public bool IsCacheMethod(IMethodSymbol method)
    {
        if (!_generatorMethodNames.Contains(method.Name))
        {
            return false;
        }

        // TryGetValue first: GetOrAdd with a capturing lambda would allocate on every call.
        var definition = method.OriginalDefinition;
        return _cacheMethods.TryGetValue(definition, out var known) ? known : _cacheMethods.GetOrAdd(definition, IsCacheMethodCore(definition));
    }

    /// <summary>Whether <paramref name="type"/> is a <c>Func&lt;..., CancellationToken, Task&lt;...&gt;&gt;</c> or its <c>ValueTask</c> form.</summary>
    public bool IsGenerator(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, Name: "Func" } func || func.TypeArguments.Length < 2)
        {
            return false;
        }

        var count = func.TypeArguments.Length;
        return SymbolEqualityComparer.Default.Equals(func.TypeArguments[count - 2], _cancellationToken)
            && func.TypeArguments[count - 1] is INamedTypeSymbol result
            && (SymbolEqualityComparer.Default.Equals(result.OriginalDefinition, _task) || SymbolEqualityComparer.Default.Equals(result.OriginalDefinition, _valueTask));
    }

    /// <summary>The state sibling of <paramref name="definition"/>, or null when it has none.</summary>
    public Sibling? StateSibling(IMethodSymbol definition, int generatorOrdinal)
    {
        var key = ((ISymbol)definition, generatorOrdinal);
        return _siblings.TryGetValue(key, out var known) ? known : _siblings.GetOrAdd(key, FindSibling(definition, generatorOrdinal));
    }

    private static ImmutableArray<IParameterSymbol> OwnParameters(IMethodSymbol method) =>
        method.IsExtensionMethod ? method.Parameters.RemoveAt(0) : method.Parameters;

    /// <summary>Matches <paramref name="candidate"/> to <paramref name="closure"/>'s parameters, plus a state parameter before <paramref name="generator"/> when it is not negative; returns the state's name, the empty string for a plain match, or null.</summary>
    private static string? Match(IMethodSymbol closure, IMethodSymbol candidate, int generator) =>
        Match(closure, candidate, generator, out _);

    private static string? Match(IMethodSymbol closure, IMethodSymbol candidate, int generator, out Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        var parameters = OwnParameters(closure);
        var others = OwnParameters(candidate);
        if (others.Length != parameters.Length + (generator >= 0 ? 1 : 0))
        {
            return null;
        }

        ITypeParameterSymbol? state = null;
        if (generator >= 0 && !TryMatchState(parameters[generator], others[generator], others[generator + 1], candidate, map, out state))
        {
            return null;
        }

        var bound = map;
        if (!OtherParametersMatch(parameters, others, generator, candidate, state, map)
            || !Unify(candidate.ReturnType, closure.ReturnType, candidate, state, map)
            || !candidate.TypeParameters.All(typeParameter => SymbolEqualityComparer.Default.Equals(typeParameter, state) || bound.ContainsKey(typeParameter)))
        {
            // A type parameter the call cannot infer from the closure's arguments would leave the sibling uncallable.
            return null;
        }

        return state is null ? string.Empty : others[generator].Name;
    }

    /// <summary>Whether <paramref name="stateParameter"/> is an unconstrained type parameter of <paramref name="candidate"/> that <paramref name="siblingGenerator"/> takes first, before the closure generator's own types.</summary>
    private static bool TryMatchState(IParameterSymbol closureGenerator, IParameterSymbol stateParameter, IParameterSymbol siblingGenerator, IMethodSymbol candidate, Dictionary<ITypeParameterSymbol, ITypeSymbol> map, out ITypeParameterSymbol? state)
    {
        state = stateParameter.Type as ITypeParameterSymbol;
        if (state is not { TypeParameterKind: TypeParameterKind.Method }
            || !SymbolEqualityComparer.Default.Equals(state.DeclaringMethod, candidate)
            || IsConstrained(state)
            || siblingGenerator.Type is not INamedTypeSymbol { TypeArguments.Length: > 0 } siblingFunc
            || closureGenerator.Type is not INamedTypeSymbol closureFunc
            || !SymbolEqualityComparer.Default.Equals(siblingFunc.TypeArguments[0], state)
            || siblingFunc.Name != closureFunc.Name
            || siblingFunc.TypeArguments.Length != closureFunc.TypeArguments.Length + 1)
        {
            return false;
        }

        var own = state;
        return closureFunc.TypeArguments.Select((type, i) => Unify(siblingFunc.TypeArguments[i + 1], type, candidate, own, map)).All(matched => matched);
    }

    // A constraint the captured values might not meet would make the fix not compile.
    private static bool IsConstrained(ITypeParameterSymbol typeParameter) =>
        typeParameter.HasReferenceTypeConstraint || typeParameter.HasValueTypeConstraint || typeParameter.HasUnmanagedTypeConstraint
        || typeParameter.HasNotNullConstraint || typeParameter.HasConstructorConstraint || typeParameter.ConstraintTypes.Length > 0;

    private static bool OtherParametersMatch(ImmutableArray<IParameterSymbol> parameters, ImmutableArray<IParameterSymbol> others, int generator, IMethodSymbol candidate, ITypeParameterSymbol? state, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i != generator && !SameParameter(parameters[i], others[generator >= 0 && i > generator ? i + 1 : i], candidate, state, map))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameParameter(IParameterSymbol parameter, IParameterSymbol other, IMethodSymbol candidate, ITypeParameterSymbol? state, Dictionary<ITypeParameterSymbol, ITypeSymbol> map) =>
        other.Name == parameter.Name && other.IsOptional == parameter.IsOptional && other.RefKind == parameter.RefKind && Unify(other.Type, parameter.Type, candidate, state, map);

    /// <summary>Whether <paramref name="candidate"/>'s type, its own type parameters bound as it goes, is <paramref name="closure"/>'s.</summary>
    private static bool Unify(ITypeSymbol candidate, ITypeSymbol closure, IMethodSymbol owner, ITypeParameterSymbol? state, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (candidate is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } typeParameter && SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, owner))
        {
            if (SymbolEqualityComparer.Default.Equals(typeParameter, state))
            {
                return false;
            }

            if (map.TryGetValue(typeParameter, out var bound))
            {
                return SymbolEqualityComparer.Default.Equals(bound, closure);
            }

            map[typeParameter] = closure;
            return true;
        }

        return (candidate, closure) switch
        {
            (INamedTypeSymbol { IsGenericType: true } generic, INamedTypeSymbol other) =>
                SymbolEqualityComparer.Default.Equals(generic.OriginalDefinition, other.OriginalDefinition)
                && generic.TypeArguments.Zip(other.TypeArguments, (a, b) => (a, b)).All(pair => Unify(pair.a, pair.b, owner, state, map)),
            (IArrayTypeSymbol array, IArrayTypeSymbol other) => array.Rank == other.Rank && Unify(array.ElementType, other.ElementType, owner, state, map),
            _ => SymbolEqualityComparer.Default.Equals(candidate, closure),
        };
    }

    // Spelled type arguments can take the state's at the end only when the others line up one for one with the closure's.
    private static bool StateTypeIsLast(IMethodSymbol closure, IMethodSymbol candidate, int generator, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        var typeParameters = candidate.TypeParameters;
        return typeParameters.Length == closure.TypeParameters.Length + 1
            && SymbolEqualityComparer.Default.Equals(typeParameters[typeParameters.Length - 1], OwnParameters(candidate)[generator].Type)
            && closure.TypeParameters.Select((typeParameter, i) => map.TryGetValue(typeParameters[i], out var bound) && SymbolEqualityComparer.Default.Equals(bound, typeParameter)).All(lined => lined);
    }

    private static Sibling? FirstSibling(IMethodSymbol closure, IEnumerable<IMethodSymbol> candidates, int generator, bool throughInterface)
    {
        foreach (var candidate in candidates)
        {
            if (Match(closure, candidate, generator, out var map) is { Length: > 0 } name)
            {
                return new Sibling(name, StateTypeIsLast(closure, candidate, generator, map), throughInterface || candidate.ContainingType.TypeKind == TypeKind.Interface);
            }
        }

        return null;
    }

    private static ITypeSymbol Receiver(IMethodSymbol definition) =>
        definition.IsExtensionMethod ? definition.Parameters[0].Type : definition.ContainingType;

    private static IEnumerable<INamedTypeSymbol> SelfAndBases(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private bool IsCacheType(ITypeSymbol type) =>
        _cacheTypes.Any(cache => SymbolEqualityComparer.Default.Equals(cache, type.OriginalDefinition));

    private IEnumerable<INamedTypeSymbol> CacheInterfaces(INamedTypeSymbol type) =>
        type.AllInterfaces.Where(IsCacheType);

    private bool IsCacheMethodCore(IMethodSymbol definition)
    {
        if (SymbolEqualityComparer.Default.Equals(definition.ContainingNamespace, _namespace) && IsCacheType(Receiver(definition)))
        {
            return true;
        }

        // A class member that implements (or, by its signature, hides) a cache member.
        return definition is { IsStatic: false, ContainingType: { TypeKind: TypeKind.Class or TypeKind.Struct } type }
            && CacheInterfaces(type).Any(cache => cache.GetMembers(definition.Name).OfType<IMethodSymbol>().Any(member =>
                SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, definition)
                || Match(definition, member, generator: -1) is not null));
    }

    private Sibling? FindSibling(IMethodSymbol closure, int generatorOrdinal)
    {
        var generator = generatorOrdinal - (closure.IsExtensionMethod ? 1 : 0);
        var receiver = (INamedTypeSymbol)Receiver(closure);

        // A class whose own members have no state overload still has the cache interface's, through a cast.
        return FirstSibling(closure, ReachableCandidates(closure.Name, receiver), generator, throughInterface: false)
            ?? (receiver.TypeKind == TypeKind.Interface
                ? null
                : FirstSibling(closure, CacheInterfaces(receiver).SelectMany(cache => cache.GetMembers(closure.Name).OfType<IMethodSymbol>()), generator, throughInterface: true));
    }

    /// <summary>The overloads a call on <paramref name="receiver"/> can reach: its own visible members and the public extensions beside the cache types.</summary>
    private IEnumerable<IMethodSymbol> ReachableCandidates(string name, INamedTypeSymbol receiver)
    {
        var isInterface = receiver.TypeKind == TypeKind.Interface;
        var receivers = isInterface ? new[] { receiver }.Concat(receiver.AllInterfaces) : SelfAndBases(receiver).Concat(receiver.AllInterfaces);
        var receiverDefinitions = receivers.Select(type => type.OriginalDefinition).ToImmutableHashSet<ISymbol>(SymbolEqualityComparer.Default);
        var members = (isInterface ? receivers : SelfAndBases(receiver))
            .SelectMany(type => type.GetMembers(name).OfType<IMethodSymbol>())
            .Where(method => !method.IsStatic && (isInterface || method.DeclaredAccessibility == Accessibility.Public));
        var extensions = _namespace.GetTypeMembers()
            .Where(type => type.IsStatic && type.MightContainExtensionMethods && type.DeclaredAccessibility == Accessibility.Public)
            .SelectMany(type => type.GetMembers(name).OfType<IMethodSymbol>())
            .Where(method => method.IsExtensionMethod && method.DeclaredAccessibility == Accessibility.Public && receiverDefinitions.Contains(method.Parameters[0].Type.OriginalDefinition));
        return members.Concat(extensions);
    }

    private sealed class GeneratorKeyComparer : IEqualityComparer<(ISymbol Method, int Ordinal)>
    {
        public static readonly GeneratorKeyComparer Instance = new();

        public bool Equals((ISymbol Method, int Ordinal) x, (ISymbol Method, int Ordinal) y) =>
            x.Ordinal == y.Ordinal && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

        public int GetHashCode((ISymbol Method, int Ordinal) obj) =>
            (SymbolEqualityComparer.Default.GetHashCode(obj.Method) * 31) + obj.Ordinal;
    }
}
