using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>An owner and argument call must raise no nullability warning wherever the equivalent tuple state call raises none.</summary>
public class OwnerArgumentNullabilityTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using UiPath.Caching;

        public class Foo { }

        public class Reader
        {
            private readonly ICache<Foo?> _nullable = null!;
            private readonly ICache<Foo> _nonNull = null!;
            private readonly ICache _untyped = null!;
            private readonly IHashCache<Foo> _hash = null!;
            private readonly IHashCache<Foo?> _nullableHash = null!;
            private readonly IHashCache _untypedHash = null!;
            private readonly CachePolicy _policy = null!;

            public Task<Foo?> LoadNullableAsync(Guid id, CancellationToken ct) => Task.FromResult<Foo?>(null);
            public Task<Foo> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult(new Foo());
            public static Task<Foo?> Load(Reader r, Guid id, CancellationToken ct) => r.LoadNullableAsync(id, ct);
            public Task<IDictionary<string, Foo?>> LoadAllNullableAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<IDictionary<string, Foo?>> LoadAllAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

            public async Task RunAsync(Guid id, string name, CancellationToken ct)
            {
                //OA nullable receiver, nullable generator, interpolated key, TimeSpan
                await _nullable.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadNullableAsync(a, t), TimeSpan.FromSeconds(1), ct);
                //TS nullable receiver, nullable generator, interpolated key, TimeSpan
                await _nullable.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadNullableAsync(s.A, t), TimeSpan.FromSeconds(1), ct);
                //OA nullable receiver, nullable generator, CacheKey
                await _nullable.GetOrAddAsync(new CacheKey(name), this, id, static (r, a, t) => r.LoadNullableAsync(a, t), ct);
                //TS nullable receiver, nullable generator, CacheKey
                await _nullable.GetOrAddAsync(new CacheKey(name), (R: this, A: id), static (s, t) => s.R.LoadNullableAsync(s.A, t), ct);
                //OA non-null receiver, nullable generator
                await _nonNull.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadNullableAsync(a, t), ct);
                //TS non-null receiver, nullable generator
                await _nonNull.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadNullableAsync(s.A, t), ct);
                //OA untyped, nullable generator, policy
                await _untyped.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadNullableAsync(a, t), _policy, ct);
                //TS untyped, nullable generator, policy
                await _untyped.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadNullableAsync(s.A, t), _policy, ct);
                //OA untyped, explicit T, nullable generator
                await _untyped.GetOrAddAsync<Foo, Reader, Guid>(new CacheKey(name), this, id, static (r, a, t) => r.LoadNullableAsync(a, t), _policy, ct);
                //TS untyped, explicit T, nullable generator
                await _untyped.GetOrAddAsync<Foo, (Reader R, Guid A)>(new CacheKey(name), (this, id), static (s, t) => s.R.LoadNullableAsync(s.A, t), _policy, ct);
                //OA nullable hash
                await _nullableHash.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadAllNullableAsync(a, t), ct);
                //TS nullable hash
                await _nullableHash.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadAllNullableAsync(s.A, t), ct);
                //OA nullable result
                await _nullable.GetOrAddResultAsync($"{id}", this, id, static (r, a, t) => r.LoadNullableAsync(a, t), TimeSpan.FromSeconds(1), ct);
                //TS nullable result
                await _nullable.GetOrAddResultAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadNullableAsync(s.A, t), TimeSpan.FromSeconds(1), ct);
                //OA async lambda
                await _nullable.GetOrAddAsync($"{id}", this, id, static async (r, a, t) => await r.LoadNullableAsync(a, t), ct);
                //TS async lambda
                await _nullable.GetOrAddAsync($"{id}", (R: this, A: id), static async (s, t) => await s.R.LoadNullableAsync(s.A, t), ct);
                //OA method group
                await _nullable.GetOrAddAsync($"{id}", this, id, Load, ct);
                //OA hash
                await _hash.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadAllAsync(a, t), ct);
                //TS hash
                await _hash.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadAllAsync(s.A, t), ct);
                //OA untyped hash
                await _untypedHash.GetOrAddAsync($"{id}", this, id, static (r, a, t) => r.LoadAllAsync(a, t), _policy, ct);
                //TS untyped hash
                await _untypedHash.GetOrAddAsync($"{id}", (R: this, A: id), static (s, t) => s.R.LoadAllAsync(s.A, t), _policy, ct);
            }
        }
        """;

    [Fact]
    public void Every_shape_that_is_warning_free_with_tuple_state_is_warning_free_with_owner_and_argument()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp13), cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "OwnerArgumentNullability",
            [tree],
            Verify.RuntimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var lines = Source.Split('\n');

        var diagnostics = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => $"{d.Id} at '{Label(lines, d.Location.GetLineSpan().StartLinePosition.Line)}'")
            .ToList();

        Assert.Empty(diagnostics);
    }

    private static string Label(string[] lines, int line)
    {
        for (var i = line; i >= 0; i--)
        {
            var text = lines[i].Trim();
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                return text[2..];
            }
        }

        return lines[line].Trim();
    }
}
