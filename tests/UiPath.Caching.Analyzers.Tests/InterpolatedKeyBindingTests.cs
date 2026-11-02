using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>Which cache member a key argument binds to at C# 12 and C# 13, so an interpolated key cannot start binding differently unnoticed.</summary>
public class InterpolatedKeyBindingTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using UiPath.Caching;

        public class Caller
        {
            private const string Const = "c";

            public async Task RunAsync(ICache cache, ICache<string> typed, IHashCache hash, string s, int id, string variable, CancellationToken ct)
            {
                await cache.GetAsync<string>($"{s}:{id}", null, ct);
                await cache.GetAsync<string>($"fixed", null, ct);
                await cache.GetAsync<string>($"{Const}:x", null, ct);
                await cache.GetAsync<string>(default, null, ct);
                await cache.GetAsync<string>("lit", null, ct);
                await cache.GetAsync<string>(variable, null, ct);
                await typed.GetAsync($"{s}:{id}", ct);
                await typed.GetAsync($"fixed", ct);
                await typed.GetAsync($"{Const}:x", ct);
                await typed.GetAsync(default, ct);
                await typed.GetAsync("lit", ct);
                await typed.GetAsync(variable, ct);
                await hash.GetAsync<string>($"{s}:{id}", null, ct);
                await hash.GetAsync<string>($"fixed", null, ct);
                await hash.GetAsync<string>($"{Const}:x", null, ct);
                await hash.GetAsync<string>(default, null, ct);
                await hash.GetAsync<string>("lit", null, ct);
                await hash.GetAsync<string>(variable, null, ct);
            }
        }
        """;

    // Statement lines in Source, by the order of the calls above: three interfaces, six forms each.
    private static readonly string[] Forms = ["interpolated", "fixed", "constant-hole", "default", "literal", "variable"];

    [Fact]
    public void At_C_sharp_13_every_form_binds_and_a_non_constant_or_constant_interpolation_takes_the_handler_member()
    {
        var (errors, bindings) = Bind(LanguageVersion.CSharp13);

        Assert.Empty(errors);
        AssertHandlerForms(bindings, "interpolated", "fixed", "constant-hole");
        AssertCacheKeyForms(bindings, "default", "literal", "variable");
    }

    [Fact]
    public void At_C_sharp_12_only_the_constant_interpolations_are_ambiguous()
    {
        var (errors, bindings) = Bind(LanguageVersion.CSharp12);

        Assert.All(errors, e => Assert.Equal("CS0121", e.Id));
        var lines = Source.Split((char)10);
        var constantInterpolations = errors.Where(e => e.GetMessage(CultureInfo.InvariantCulture).Contains("CacheKeyHandler", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, constantInterpolations.Length);
        Assert.All(constantInterpolations, e =>
        {
            var line = lines[e.Location.GetLineSpan().StartLinePosition.Line];
            Assert.True(line.Contains("$\"fixed\"") || line.Contains("$\"{Const}:x\""), line);
        });

        // As on 3.0: a bare default is ambiguous between CacheKey and CacheKey[] on the two cache interfaces that have both; it never involves the handler.
        var others = errors.Except(constantInterpolations).ToArray();
        Assert.Equal(2, others.Length);
        Assert.All(others, e => Assert.Contains("CacheKey[]", e.GetMessage(CultureInfo.InvariantCulture)));
        AssertHandlerForms(bindings, "interpolated");
        AssertCacheKeyForms(bindings, "literal", "variable");
    }

    [Fact]
    public void In_an_expression_tree_an_interpolated_key_does_not_compile_and_a_string_key_does()
    {
        const string interpolated = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;
            using UiPath.Caching;

            public static class Setup
            {
                public static Expression<Func<ICache, ValueTask<string?>>> Get(int id) => c => c.GetAsync<string>($"user:{id}", null, default);
            }
            """;
        var asString = interpolated.Replace("$\"user:{id}\"", "\"user:\" + id", StringComparison.Ordinal);

        Assert.Contains(Compile(interpolated, LanguageVersion.CSharp13), e => e.Id == "CS8952");
        Assert.Empty(Compile(asString, LanguageVersion.CSharp13));
    }

    private static List<Diagnostic> Compile(string source, LanguageVersion version) =>
        CSharpCompilation.Create(
                "ExpressionTree",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version))],
                Verify.RuntimeReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static (List<Diagnostic> Errors, Dictionary<string, IMethodSymbol?> Bindings) Bind(LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(version));
        var compilation = CSharpCompilation.Create(
            "Binding",
            [tree],
            Verify.RuntimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        var calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.ArgumentList.Arguments.Count > 0 && c.Expression.ToString().Contains("GetAsync", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(18, calls.Length);

        var bindings = new Dictionary<string, IMethodSymbol?>();
        for (var i = 0; i < calls.Length; i++)
        {
            var info = model.GetSymbolInfo(calls[i]);
            bindings[$"{i / 6}:{Forms[i % 6]}"] = info.Symbol as IMethodSymbol;
        }

        return (errors, bindings);
    }

    private static void AssertHandlerForms(Dictionary<string, IMethodSymbol?> bindings, params string[] forms)
    {
        foreach (var form in forms)
        {
            for (var cache = 0; cache < 3; cache++)
            {
                var method = bindings[$"{cache}:{form}"];
                Assert.NotNull(method);
                Assert.Equal("CacheKeyHandler", method.Parameters[0].Type.Name);
                Assert.Equal(RefKind.Ref, method.Parameters[0].RefKind);
            }
        }
    }

    private static void AssertCacheKeyForms(Dictionary<string, IMethodSymbol?> bindings, params string[] forms)
    {
        foreach (var form in forms)
        {
            for (var cache = 0; cache < 3; cache++)
            {
                var method = bindings[$"{cache}:{form}"];
                Assert.NotNull(method);
                Assert.Equal("CacheKey", method.Parameters[0].Type.Name);
            }
        }
    }
}
