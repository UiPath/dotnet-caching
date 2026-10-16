using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>Which state overload the analyzer pairs with a closure overload, read straight from <see cref="CacheMethods"/>.</summary>
public class StateSiblingTests
{
    [Theory]
    [InlineData("where TState : struct", true)]
    [InlineData("where TState : class", true)]
    [InlineData("", false)]
    public void A_class_state_overload_counts_only_without_a_constraint(string constraint, bool onlyTheInterfaceOne)
    {
        var members = $$"""
                public ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
                    {{constraint}} => default;
            """;
        AssertSibling(members, onlyTheInterfaceOne);
    }

    [Theory]
    [InlineData("string", "\"\"", true)]
    [InlineData("void", "{ }", true)]
    [InlineData("ValueTask<T?>", "default", false)]
    public void A_class_state_overload_counts_only_with_the_closure_return_type(string returnType, string body, bool onlyTheInterfaceOne)
    {
        var members = $$"""
                public {{returnType}} GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default){{(body == "{ }" ? " { }" : " => " + body + ";")}}
            """;
        AssertSibling(members, onlyTheInterfaceOne);
    }

    [Fact]
    public void A_lookup_for_another_parameter_does_not_answer_for_the_generator()
    {
        var methods = CacheMethods.Create(Compile(string.Empty, out var closure))!;

        // The key is the method and the parameter: a miss on the key (ordinal 0) is not the generator's answer (ordinal 1).
        Assert.Null(methods.StateSibling(closure, generatorOrdinal: 0));
        Assert.NotNull(methods.StateSibling(closure, generatorOrdinal: 1));
    }

    private static void AssertSibling(string members, bool onlyTheInterfaceOne)
    {
        var sibling = CacheMethods.Create(Compile(members, out var closure))!.StateSibling(closure, generatorOrdinal: 1);

        Assert.NotNull(sibling);
        Assert.Equal("state", sibling.ParameterName);
        Assert.Equal(onlyTheInterfaceOne, sibling.IsInterfaceMember);
    }

    // A RegionCache<T> of the test's own with the members given, and its CacheKey closure overload.
    private static CSharpCompilation Compile(string members, out IMethodSymbol closure)
    {
        var source = "using System;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing UiPath.Caching;\n#nullable enable\n" + CacheImplementation.Abstract("UiPath.Caching.ICache`1", "RegionCache<T>", members);
        var compilation = CSharpCompilation.Create("sibling", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)], Verify.RuntimeReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        closure = compilation.GetTypeByMetadataName("RegionCache`1")!.GetMembers("GetOrAddAsync").OfType<IMethodSymbol>()
            .Single(method => method.Parameters.Length == 3 && method.Parameters[0].Type.Name == "CacheKey" && method.Parameters[1].Type.Name == "Func");
        return compilation;
    }
}
