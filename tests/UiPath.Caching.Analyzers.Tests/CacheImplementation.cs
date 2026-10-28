using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>Source for a cache class of the test's own: every abstract member of a cache interface, declared abstract, plus the members a test adds.</summary>
internal static class CacheImplementation
{
    private static readonly SymbolDisplayFormat Declaration = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>`public abstract class {declaration} : {interface}` with the interface's abstract members and <paramref name="members"/>.</summary>
    /// <remarks>An interface method whose signature one of <paramref name="members"/> declares is implemented explicitly, so the class sees only its own.</remarks>
    public static string Abstract(string interfaceMetadataName, string declaration, string members)
    {
        var compilation = CSharpCompilation.Create("probe", references: Verify.RuntimeReferences);
        var cache = compilation.GetTypeByMetadataName(interfaceMetadataName)!;
        var declared = Signatures(members);
        var abstracts = new[] { cache }.Concat(cache.AllInterfaces)
            .SelectMany(type => type.GetMembers())
            .Where(member => member.IsAbstract && member is IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
            .Select(member => member is IMethodSymbol method && declared.Contains(Signature(method))
                ? "    " + Explicit(method)
                : "    public abstract " + member.ToDisplayString(Declaration) + (member is IMethodSymbol ? ";" : string.Empty));
        return $"public abstract class {declaration} : {cache.ToDisplayString()}\n{{\n{string.Join("\n", abstracts)}\n\n{members}\n}}\n";
    }

    private static string Explicit(IMethodSymbol method)
    {
        var typeParameters = method.TypeParameters.IsEmpty ? string.Empty : "<" + string.Join(", ", method.TypeParameters.Select(type => type.Name)) + ">";
        var parameters = string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(Declaration) + " " + parameter.Name));
        // An explicit implementation reads an unconstrained `T?` as Nullable<T> unless the type parameter says `default`.
        var defaults = string.Concat(method.TypeParameters.Where(Unconstrained).Select(type => $" where {type.Name} : default"));
        return $"{method.ReturnType.ToDisplayString(Declaration)} {method.ContainingType.ToDisplayString(Declaration)}.{method.Name}{typeParameters}({parameters}){defaults} => throw null!;";
    }

    private static bool Unconstrained(ITypeParameterSymbol type) =>
        !type.HasReferenceTypeConstraint && !type.HasValueTypeConstraint && !type.HasNotNullConstraint && !type.HasUnmanagedTypeConstraint && type.ConstraintTypes.IsEmpty;

    // Name, type parameter count and parameter types, without namespaces or nullable marks, so a member written in a test matches the symbol.
    private static string Signature(IMethodSymbol method) =>
        Normalize($"{method.Name}`{method.TypeParameters.Length}({string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))})");

    private static HashSet<string> Signatures(string members) =>
        CSharpSyntaxTree.ParseText("class C {" + members + "}").GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(method => Normalize($"{method.Identifier.Text}`{method.TypeParameterList?.Parameters.Count ?? 0}({string.Join(",", method.ParameterList.Parameters.Select(parameter => parameter.Type!.ToString()))})"))
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string signature) => signature.Replace("?", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
}
