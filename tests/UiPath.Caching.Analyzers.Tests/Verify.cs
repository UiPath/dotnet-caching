using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>Runs UIPCACHE001 and its fix over a source that compiles against this test's runtime and UiPath.Caching.Abstractions.</summary>
internal static class Verify
{
    private const string Usings = """
        #nullable enable
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using UiPath.Caching;

        """;

    // The running runtime's assemblies (UiPath.Caching.Abstractions among them): no reference pack to download.
    private static readonly ImmutableArray<MetadataReference> References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

    /// <summary>The running runtime's assemblies, for a test that builds its own compilation.</summary>
    internal static ImmutableArray<MetadataReference> RuntimeReferences => References;

    public static DiagnosticResult Diagnostic(int location, string allocation, string advice) =>
        new DiagnosticResult(CapturingGeneratorAnalyzer.Rule).WithLocation(location).WithArguments(allocation, advice);

    /// <summary>Checks the diagnostics, then that the fix turns <paramref name="source"/> into <paramref name="fixedSource"/> (pass the source itself when no fix applies).</summary>
    public static Task FixAsync(string source, string fixedSource, params DiagnosticResult[] expected) =>
        FixAsync(source, fixedSource, numberOfFixAllIterations: null, expected);

    public static Task FixAsync(string source, string fixedSource, int? numberOfFixAllIterations, params DiagnosticResult[] expected) =>
        FixWholeFileAsync(Usings + source, Usings + fixedSource, numberOfFixAllIterations, expected);

    /// <summary>As <see cref="FixAsync(string, string, DiagnosticResult[])"/>, for a source that brings its own usings.</summary>
    public static Task FixWholeFileAsync(string source, string fixedSource, int? numberOfFixAllIterations, params DiagnosticResult[] expected) =>
        FixFilesAsync([source], [fixedSource], numberOfFixAllIterations, expected);

    /// <summary>As <see cref="FixAsync(string, string, DiagnosticResult[])"/>, over several files, each given its usings.</summary>
    public static Task FixFilesAsync(string[] sources, string[] fixedSources, params DiagnosticResult[] expected) =>
        FixFilesAsync([.. sources.Select(source => Usings + source)], [.. fixedSources.Select(source => Usings + source)], null, expected);

    public static Task NoDiagnosticAsync(string source) => FixAsync(source, source);

    /// <summary>Checks the compiler errors at <paramref name="version"/>, then that the interpolated-key fix turns <paramref name="source"/> into <paramref name="fixedSource"/>.</summary>
    public static Task FixCompilerErrorAsync(string source, string fixedSource, LanguageVersion version, params DiagnosticResult[] expected) =>
        FixCompilerErrorFilesAsync([source], [fixedSource], version, usingSystem: true, numberOfFixAllIterations: null, expected);

    /// <summary>As <see cref="FixCompilerErrorAsync"/>, over several files; <paramref name="usingSystem"/> false drops <c>using System;</c> from their usings.</summary>
    public static Task FixCompilerErrorFilesAsync(string[] sources, string[] fixedSources, LanguageVersion version, bool usingSystem, int? numberOfFixAllIterations, params DiagnosticResult[] expected)
    {
        var usings = usingSystem ? Usings : string.Join("\n", Usings.Split('\n').Where(line => line.TrimEnd() != "using System;"));
        var test = new CompilerErrorFixTest
        {
            ReferenceAssemblies = new ReferenceAssemblies("runtime"),
            NumberOfFixAllIterations = numberOfFixAllIterations,
        };
        for (var i = 0; i < sources.Length; i++)
        {
            test.TestState.Sources.Add(($"/0/Test{i}.cs", usings + sources[i]));
            test.FixedState.Sources.Add(($"/0/Test{i}.cs", usings + fixedSources[i]));
        }

        test.TestState.AdditionalReferences.AddRange(References);
        test.SolutionTransforms.Add((solution, project) =>
            solution.WithProjectParseOptions(project, ((CSharpParseOptions)solution.GetProject(project)!.ParseOptions!).WithLanguageVersion(version)));
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(TestContext.Current.CancellationToken);
    }


    private static Task FixFilesAsync(string[] sources, string[] fixedSources, int? numberOfFixAllIterations, DiagnosticResult[] expected)
    {
        // The state is named, never Item1, Item2: the fix writes tuple elements by the captured names.
        Assert.All(fixedSources, fixedSource => Assert.DoesNotMatch(@"Item[1-9]", fixedSource));
        var test = new CSharpCodeFixTest<CapturingGeneratorAnalyzer, PassStateCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = new ReferenceAssemblies("runtime"),
            NumberOfFixAllIterations = numberOfFixAllIterations,
        };
        for (var i = 0; i < sources.Length; i++)
        {
            test.TestState.Sources.Add(($"/0/Test{i}.cs", sources[i]));
            test.FixedState.Sources.Add(($"/0/Test{i}.cs", fixedSources[i]));
        }

        test.TestState.AdditionalReferences.AddRange(References);
        test.SolutionTransforms.Add((solution, project) =>
            solution.WithProjectCompilationOptions(project, ((CSharpCompilationOptions)solution.GetProject(project)!.CompilationOptions!).WithAllowUnsafe(true)));
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    // A handler in an expression tree also reports CS8640 on the CS8952 span; the fix is offered on CS8952, so the tests leave it out.
    private sealed class CompilerErrorFixTest : CSharpCodeFixTest<EmptyDiagnosticAnalyzer, InterpolatedKeyCodeFixProvider, DefaultVerifier>
    {
        protected override ImmutableArray<(Project project, Diagnostic diagnostic)> FilterDiagnostics(ImmutableArray<(Project project, Diagnostic diagnostic)> diagnostics) =>
            [.. base.FilterDiagnostics(diagnostics).Where(d => d.diagnostic.Id != "CS8640")];
    }
}
