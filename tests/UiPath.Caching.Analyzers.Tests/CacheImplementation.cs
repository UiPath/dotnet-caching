using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
    public static string Abstract(string interfaceMetadataName, string declaration, string members)
    {
        var compilation = CSharpCompilation.Create("probe", references: Verify.RuntimeReferences);
        var cache = compilation.GetTypeByMetadataName(interfaceMetadataName)!;
        var abstracts = new[] { cache }.Concat(cache.AllInterfaces)
            .SelectMany(type => type.GetMembers())
            .Where(member => member.IsAbstract && member is IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
            .Select(member => "    public abstract " + member.ToDisplayString(Declaration) + (member is IMethodSymbol ? ";" : string.Empty));
        return $"public abstract class {declaration} : {cache.ToDisplayString()}\n{{\n{string.Join("\n", abstracts)}\n\n{members}\n}}\n";
    }
}
