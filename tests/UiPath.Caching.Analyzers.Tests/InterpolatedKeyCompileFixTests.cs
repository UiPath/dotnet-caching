using System.Globalization;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

public class InterpolatedKeyCompileFixTests
{
    private const string MinusSignCulture = "en-US with U+2212 minus";

    public static TheoryData<string, string, string> Rewrites()
    {
        (string Original, string Rewritten)[] rewrites =
        [
            ("$\"fixed\"", "\"fixed\""),
            ("$\"{Const}:x\"", "Const + \":x\""),
            ("$\"a{{b}}\"", "\"a{b}\""),
            ("$@\"p\\{{z\"", "\"p\\\\{z\""),
            ("$\"{nameof(Const)}:k\"", "nameof(Const) + \":k\""),
            ("$\"a{$\"{Const}\"}\"", "\"a\" + Const"),
            ("$\"{{{Const}}}\"", "\"{\" + Const + \"}\""),
            ("$\"a{{{Const}}}b{{\"", "\"a{\" + Const + \"}b{\""),
            ("$\"{(true ? Const : \"d\")}:k\"", "(true ? Const : \"d\") + \":k\""),
            ("$\"user:{id}\"", "FormattableString.Invariant($\"user:{id}\")"),
            ("$\"o:{d:F2}:{id,5}\"", "FormattableString.Invariant($\"o:{d:F2}:{id,5}\")"),
            ("$@\"p\\{id}\"", "FormattableString.Invariant($@\"p\\{id}\")"),
        ];
        var data = new TheoryData<string, string, string>();
        foreach (var culture in new[] { "de-DE", "en-US", MinusSignCulture })
        {
            foreach (var (original, rewritten) in rewrites)
            {
                data.Add(culture, original, rewritten);
            }
        }

        return data;
    }

    [Theory]
    [InlineData("cache.{|CS0121:GetAsync<string>|}($\"fixed\", null, ct)", "cache.GetAsync<string>(\"fixed\", null, ct)")]
    [InlineData("typed.{|CS0121:GetAsync|}($\"fixed\", ct)", "typed.GetAsync(\"fixed\", ct)")]
    [InlineData("hash.{|CS0121:GetAsync<string>|}($\"fixed\", null, ct)", "hash.GetAsync<string>(\"fixed\", null, ct)")]
    [InlineData("typedHash.{|CS0121:GetAsync|}($\"fixed\", ct)", "typedHash.GetAsync(\"fixed\", ct)")]
    [InlineData("cache.{|CS0121:GetAsync<string>|}($\"{Const}:x\", null, ct)", "cache.GetAsync<string>(Const + \":x\", null, ct)")]
    [InlineData("typed.{|CS0121:GetAsync|}($\"{Const}:x\", ct)", "typed.GetAsync(Const + \":x\", ct)")]
    [InlineData("hash.{|CS0121:GetAsync<string>|}($\"{Const}:x\", null, ct)", "hash.GetAsync<string>(Const + \":x\", null, ct)")]
    [InlineData("typedHash.{|CS0121:GetAsync|}($\"{Const}:x\", ct)", "typedHash.GetAsync(Const + \":x\", ct)")]
    public Task Constant_keys_become_plain_strings_on_every_interface(string call, string fixedCall) => FixAsync(call, fixedCall);

    [Theory]
    [InlineData("$\"a{{b}}\"", "\"a{b}\"")]
    [InlineData("$\"q\\\"x\"", "\"q\\\"x\"")]
    [InlineData("$@\"p\\{{z\"", "\"p\\\\{z\"")]
    [InlineData("$\"\"\"r\\s\"\"\"", "\"r\\\\s\"")]
    public Task Escapes_are_rebuilt_from_the_value(string key, string fixedKey) => FixAsync(KeyCall(key, marked: true), KeyCall(fixedKey, marked: false));

    [Theory]
    [InlineData("$\"{Const}\"", "Const")]
    [InlineData("$\"{nameof(Const)}:k\"", "nameof(Const) + \":k\"")]
    [InlineData("$\"a{$\"{Const}\"}\"", "\"a\" + Const")]
    [InlineData("$\"{$\"{Const}\"}\"", "Const")]
    [InlineData("$\"{Const /*tenant*/}:x\"", "Const /*tenant*/ + \":x\"")]
    [InlineData("$\"{(true ? Const : \"d\")}:k\"", "(true ? Const : \"d\") + \":k\"")]
    public Task Hole_forms_concatenate_as_written(string key, string fixedKey) => FixAsync(KeyCall(key, marked: true), KeyCall(fixedKey, marked: false));

    [Theory]
    [InlineData("$\"{{{Const}}}\"", "\"{\" + Const + \"}\"")]
    [InlineData("$\"a{{{Const}}}b{{\"", "\"a{\" + Const + \"}b{\"")]
    [InlineData("$\"{Const}}}\"", "Const + \"}\"")]
    [InlineData("$@\"{{{Const}}}\\\"", "\"{\" + Const + \"}\\\\\"")]
    public Task Escaped_braces_next_to_a_hole_keep_the_key_text(string key, string fixedKey) => FixAsync(KeyCall(key, marked: true), KeyCall(fixedKey, marked: false));

    [Fact]
    public Task No_offer_when_the_key_is_not_what_makes_the_call_ambiguous()
    {
        // Ambiguous between the TimeSpan and DateTimeOffset expirations: a plain string key leaves it so.
        var source = Caller("await cache.{|CS0121:SetAsync<string>|}($\"fixed\", \"v\", expiration: default, policy: null, token: ct);");
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task Empty_constant_key_becomes_an_empty_string() => FixAsync(KeyCall("$\"\"", marked: true), KeyCall("\"\"", marked: false));

    [Theory]
    [InlineData("cache.{|CS0121:GetOrAddAsync<string>|}($\"fixed\", c => Task.FromResult<string?>(\"v\"), null, ct)", "cache.GetOrAddAsync<string>(\"fixed\", c => Task.FromResult<string?>(\"v\"), null, ct)")]
    [InlineData("cache.{|CS0121:GetOrAddAsync<string, int>|}($\"fixed\", 1, static (s, c) => Task.FromResult<string?>(\"v\"), null, ct)", "cache.GetOrAddAsync<string, int>(\"fixed\", 1, static (s, c) => Task.FromResult<string?>(\"v\"), null, ct)")]
    [InlineData("cache.{|CS0121:SetAsync<string>|}($\"fixed\", \"v\", null, ct)", "cache.SetAsync<string>(\"fixed\", \"v\", null, ct)")]
    [InlineData("cache.{|CS0121:RemoveAsync<string>|}($\"fixed\", ct)", "cache.RemoveAsync<string>(\"fixed\", ct)")]
    [InlineData("typedHash.{|CS0121:RemoveAsync|}($\"fixed\", ct)", "typedHash.RemoveAsync(\"fixed\", ct)")]
    public Task Every_handler_member_is_fixed(string call, string fixedCall) => FixAsync(call, fixedCall);

    [Fact]
    public Task No_offer_for_an_unrelated_ambiguity()
    {
        const string source = """
            public class Other
            {
                public void M(object a, string b) { }

                public void M(string a, object b) { }

                public void Run() => {|CS0121:M|}("x", "y");
            }
            """;
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp12);
    }

    [Fact]
    public Task No_offer_for_an_ambiguity_nested_in_a_cache_call()
    {
        const string source = """
            public class Z
            {
                public Z(object a, string b) { }

                public Z(string a, object b) { }
            }

            public class Caller
            {
                public ValueTask<string?> Run(ICache cache, CancellationToken ct) =>
                    cache.GetOrAddAsync<string>($"fixed", c => { _ = new {|CS0121:Z|}("x", "y"); return Task.FromResult<string?>("v"); }, null, ct);
            }
            """;
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task No_offer_for_a_default_key()
    {
        var source = Caller("await cache.{|CS0121:GetAsync<string>|}(default, null, ct);");
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp12);
    }

    [Theory]
    [InlineData(LanguageVersion.CSharp12)]
    [InlineData(LanguageVersion.CSharp13)]
    public Task An_interpolated_key_in_an_expression_tree_is_wrapped(LanguageVersion version) =>
        TreeFixAsync("{|CS8952:$\"user:{id}\"|}", "FormattableString.Invariant($\"user:{id}\")", version);

    [Theory]
    [InlineData(LanguageVersion.CSharp12)]
    [InlineData(LanguageVersion.CSharp13)]
    public Task Formats_and_alignments_are_kept(LanguageVersion version) =>
        // A hole with a format or an alignment also reports CS0853, from the same handler conversion.
        TreeFixAsync("{|CS8952:$\"o:{|CS0853:{d:F2}|}:{|CS0853:{id,5}|}\"|}", "FormattableString.Invariant($\"o:{d:F2}:{id,5}\")", version);

    [Theory]
    [InlineData("$@\"p\\{id}\"")]
    [InlineData("$\"\"\"r{id}\"\"\"")]
    public Task Verbatim_and_raw_interpolations_are_kept(string key) =>
        TreeFixAsync($"{{|CS8952:{key}|}}", $"FormattableString.Invariant({key})", LanguageVersion.CSharp13);

    [Fact]
    public Task Without_using_System_the_fix_still_compiles() =>
        Verify.FixCompilerErrorFilesAsync(
            [Tree("{|CS8952:$\"user:{id}\"|}", qualified: true)],
            [Tree("System.FormattableString.Invariant($\"user:{id}\")", qualified: true)],
            LanguageVersion.CSharp13,
            usingSystem: false,
            numberOfFixAllIterations: null);

    [Theory]
    [InlineData(LanguageVersion.CSharp12, "CS0121")]
    [InlineData(LanguageVersion.CSharp13, "CS8952")]
    public Task A_constant_key_in_an_expression_tree_gets_a_plain_string(LanguageVersion version, string error) =>
        Verify.FixCompilerErrorAsync(
            Tree(error == "CS0121" ? "$\"fixed\"" : "{|CS8952:$\"fixed\"|}", qualified: false, method: error == "CS0121" ? "{|CS0121:GetAsync<string>|}" : "GetAsync<string>"),
            Tree("\"fixed\"", qualified: false),
            version);

    [Fact]
    public Task A_local_named_FormattableString_does_not_capture_the_wrap()
    {
        const string source = """
            using System.Linq.Expressions;

            public class Setup
            {
                public static Expression<Func<ICache, ValueTask<string?>>> Get(int id)
                {
                    var FormattableString = 0;
                    _ = FormattableString;
                    return c => c.GetAsync<string>({|CS8952:$"user:{id}"|}, null, default);
                }
            }
            """;
        const string fixedSource = """
            using System.Linq.Expressions;

            public class Setup
            {
                public static Expression<Func<ICache, ValueTask<string?>>> Get(int id)
                {
                    var FormattableString = 0;
                    _ = FormattableString;
                    return c => c.GetAsync<string>(System.FormattableString.Invariant($"user:{id}"), null, default);
                }
            }
            """;
        return Verify.FixCompilerErrorAsync(source, fixedSource, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task No_wrap_for_a_named_key_in_a_tree()
    {
        // A named argument is itself not allowed in an expression tree (CS0853), so the wrap would not make it compile.
        const string source = """
            using System.Linq.Expressions;

            public class Setup
            {
                public static Expression<Func<ICache, ValueTask<string?>>> Get(int id) => c => {|CS0853:c.GetAsync<string>(cacheKey: {|CS8952:$"u:{id}"|}, policy: null, token: default)|};
            }
            """;
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task No_offer_for_a_key_with_a_span_hole()
    {
        // A span cannot go into a FormattableString, so the wrap would not compile either.
        const string source = """
            using System.Linq.Expressions;

            public class Setup
            {
                public static Expression<Func<ICache, ValueTask<string?>>> Get(string id) => c => c.GetAsync<string>({|CS8952:$"user:{id.AsSpan()}"|}, null, default);
            }
            """;
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task No_offer_for_an_unrelated_handler_in_a_tree()
    {
        const string source = """
            using System.Globalization;
            using System.Linq.Expressions;

            public class Other
            {
                public Expression<Func<string>> E() => () => string.Create(CultureInfo.InvariantCulture, {|CS8952:$"x{1}"|});
            }
            """;
        return Verify.FixCompilerErrorAsync(source, source, LanguageVersion.CSharp13);
    }

    [Fact]
    public Task Fix_all_covers_every_document()
    {
        // The constant key sits in a member of its own: the compiler reports no CS8952 in a member that has a CS0121.
        static string File(string name, string keys, string constantCall) => $$"""
            using System.Linq.Expressions;

            public class {{name}}
            {
                private const string Const = "c";

                public static Expression<Func<ICache, ValueTask<string?>>>[] Get(int id, decimal d) => [{{keys}}];

                public static Task Constant(ICache cache) => {{constantCall}};
            }
            """;
        const string first = "c => c.GetAsync<string>({|CS8952:$\"a:{id}\"|}, null, default), c => c.GetAsync<string>({|CS8952:$\"b:{d}\"|}, null, default)";
        const string firstFixed = "c => c.GetAsync<string>(FormattableString.Invariant($\"a:{id}\"), null, default), c => c.GetAsync<string>(FormattableString.Invariant($\"b:{d}\"), null, default)";
        const string second = "c => c.GetAsync<string>({|CS8952:$\"c:{id}\"|}, null, default), c => c.GetAsync<string>({|CS8952:$\"e:{|CS0853:{id,3}|}\"|}, null, default)";
        const string secondFixed = "c => c.GetAsync<string>(FormattableString.Invariant($\"c:{id}\"), null, default), c => c.GetAsync<string>(FormattableString.Invariant($\"e:{id,3}\"), null, default)";
        const string constant = "cache.{|CS0121:GetAsync<string>|}($\"{Const}:x\", null, default).AsTask()";
        const string constantFixed = "cache.GetAsync<string>(Const + \":x\", null, default).AsTask()";
        const string none = "Task.CompletedTask";
        return Verify.FixCompilerErrorFilesAsync(
            [File("A", first, constant), File("B", second, none)],
            [File("A", firstFixed, constantFixed), File("B", secondFixed, none)],
            LanguageVersion.CSharp12,
            usingSystem: true,
            // Document A holds both rewrites, so Fix All takes more than one pass; at most three.
            numberOfFixAllIterations: -3);
    }

    [Theory]
    [MemberData(nameof(Rewrites))]
    public void Each_rewrite_keeps_the_key_text(string culture, string original, string rewritten)
    {
        var source = $$"""
            using System;

            public static class K
            {
                private const string Const = "c";

                public static string A(int id, decimal d) => string.Create(System.Globalization.CultureInfo.InvariantCulture, {{original}});

                public static string B(int id, decimal d) => {{rewritten}};
            }
            """;
        var compilation = CSharpCompilation.Create(
            "Keys",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            Verify.RuntimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));

        var context = new AssemblyLoadContext("keys", isCollectible: true);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            image.Position = 0;
            var keys = context.LoadFromStream(image).GetType("K")!;
            CultureInfo.CurrentCulture = Culture(culture);
            object[] arguments = [-42, 1234.5m];
            Assert.Equal(keys.GetMethod("A")!.Invoke(null, arguments), keys.GetMethod("B")!.Invoke(null, arguments));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            context.Unload();
        }
    }

    private static CultureInfo Culture(string name)
    {
        if (name != MinusSignCulture)
        {
            return CultureInfo.GetCultureInfo(name);
        }

        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.NumberFormat.NegativeSign = "−";
        return culture;
    }

    private static Task TreeFixAsync(string key, string fixedKey, LanguageVersion version) =>
        Verify.FixCompilerErrorAsync(Tree(key, qualified: false), Tree(fixedKey, qualified: false), version);

    private static string Tree(string key, bool qualified, string method = "GetAsync<string>") => $$"""
        using System.Linq.Expressions;

        public class Setup
        {
            public static Expression<{{(qualified ? "System." : string.Empty)}}Func<ICache, ValueTask<string?>>> Get(int id, decimal d) => c => c.{{method}}({{key}}, null, default);
        }
        """;

    private static Task FixAsync(string call, string fixedCall) =>
        Verify.FixCompilerErrorAsync(Caller($"await {call};"), Caller($"await {fixedCall};"), LanguageVersion.CSharp12);

    private static string KeyCall(string key, bool marked) =>
        marked ? $"cache.{{|CS0121:GetAsync<string>|}}({key}, null, ct)" : $"cache.GetAsync<string>({key}, null, ct)";

    private static string Caller(string body) => $$"""
        public class Caller
        {
            private const string Const = "c";

            public async Task RunAsync(ICache cache, ICache<string> typed, IHashCache hash, IHashCache<string> typedHash, CancellationToken ct)
            {
                {{body}}
            }
        }
        """;
}
