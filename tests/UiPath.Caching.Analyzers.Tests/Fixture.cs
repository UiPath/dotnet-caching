namespace UiPath.Caching.Analyzers.Tests;

/// <summary>The service the analyzer tests put their members in, with a cache of each kind and loaders to call.</summary>
internal static class Fixture
{
    public static string Service(string members) => $$"""
        using System.Collections.Generic;
        using System.Linq;

        public class Item { }

        public class Repo
        {
            public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
        }

        public static class RepoExtensions
        {
            public static Task<Item?> LoadOrDefaultAsync(this Repo? repo, CancellationToken ct) => Task.FromResult<Item?>(null);
        }

        public class Service
        {
            private readonly ICache<Item> _cache = null!;
            private readonly ICache _untyped = null!;
            private readonly IHashCache<Item> _hash = null!;
            private readonly IHashCache _untypedHash = null!;
            private readonly CachePolicy? _policy = null;
            private readonly Repo _repo = new();
            private readonly Func<CancellationToken, Task<Item?>> _load = LoadStaticAsync;
            private readonly int _offset = 1;
            private readonly Cache<Item> _concrete = null!;
            private readonly KeyValuePair<CacheKey, int>[] _entries = [];

            private int Offset => _offset;

            private static Task<Item?> LoadStaticAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());

            private static Task<Item?> LoadStaticAsync(int id, CancellationToken ct) => Task.FromResult<Item?>(new Item());

            private static Task<IDictionary<string, Item?>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?>());

            private Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());

            private Task<Item?> LoadAsync(int id, CancellationToken ct) => Task.FromResult<Item?>(new Item());

            private Task<IDictionary<string, Item?>> LoadAllInstanceAsync(CancellationToken ct) => LoadAllAsync(ct);

            private Task<IDictionary<string, Item?>> LoadAllInstanceAsync(int id, CancellationToken ct) => LoadAllAsync(ct);

            private Task<GeneratedValue<Item>> GenerateAsync(CancellationToken ct) => Task.FromResult(new GeneratedValue<Item>(new Item()));

            private Task<GeneratedValue<Item>> GenerateAsync(int id, CancellationToken ct) => Task.FromResult(new GeneratedValue<Item>(new Item()));

            private Task<KeyValuePair<int, Item?>[]> LoadManyAsync(int[] ids, CancellationToken ct) => Task.FromResult(ids.Select(id => new KeyValuePair<int, Item?>(id, new Item())).ToArray());

            private Task<KeyValuePair<int, Item?>[]> LoadManyAsync(int[] ids, CancellationToken ct, int extra) => LoadManyAsync(ids, ct);

        {{Indent(members)}}
        }
        """;

    private static string Indent(string members) => string.Join("\n", members.Split('\n').Select(line => line.Length == 0 ? line : "    " + line));
}
