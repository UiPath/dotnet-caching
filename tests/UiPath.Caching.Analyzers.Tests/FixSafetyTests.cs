using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>Shapes where moving the captures into state would not compile or would change what the call does: the fix stays away, or adapts.</summary>
public class FixSafetyTests
{
    private const string PassIt = "pass it as state and make the lambda static";

    [Fact]
    public Task Dynamic_capture_gets_no_fix()
    {
        // As state it would bind the call at run time, where a lambda cannot be an argument (CS1977).
        var source = Fixture.Service("""ValueTask<Item?> Get(dynamic id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync((int)id, ct)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Extension_called_through_its_class_gets_no_fix()
    {
        // The state overload is in StatefulCacheExtensions, not in the class the call names.
        var source = Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => CacheExtensions.GetOrAddAsync<Item>(_untyped, "k", {|#0:ct => LoadAsync(ct)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Anonymous_type_inside_the_state_type_is_not_spelled()
    {
        var source = Fixture.Service("""
            ValueTask<IDictionary<string, Item?>> Get(CancellationToken token)
            {
                var anonymous = new[] { new { Id = 1 } };
                return _untypedHash.GetOrAddAsync<Item>("k", {|#0:ct => LoadAllInstanceAsync(anonymous.Length, ct)|}, token);
            }

            ValueTask<IDictionary<string, Item?>> Find(CancellationToken token)
            {
                var anonymous = new { Id = 1 };
                return _untypedHash.GetOrAddAsync<Item>("k", {|#1:ct => LoadAllInstanceAsync(anonymous.Id, ct)|}, token);
            }
            """);
        return Verify.FixAsync(
            source,
            source,
            Verify.Diagnostic(0, "captures 'this' and 'anonymous' and allocates a closure", "pass them as state and make the lambda static"),
            Verify.Diagnostic(1, "captures 'this' and 'anonymous' and allocates a closure", "pass them as state and make the lambda static"));
    }

    [Fact]
    public Task Conditional_call_with_type_arguments_gets_the_state_type() =>
        Verify.FixAsync(
            Fixture.Service("""void Fire(int id, CancellationToken token) => _untypedHash?.GetOrAddAsync<Item>("k", {|#0:ct => LoadAllInstanceAsync(id, ct)|}, token);"""),
            Fixture.Service("""void Fire(int id, CancellationToken token) => _untypedHash?.GetOrAddAsync<Item, (Service Self, int Id)>("k", (Self: this, Id: id), static (s, ct) => s.Self.LoadAllInstanceAsync(s.Id, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", "pass them as state and make the lambda static"));

    [Fact]
    public Task Anonymous_method_without_parameters_gets_distinct_names() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<KeyValuePair<int, Item?>[]> Get(CancellationToken token) => _cache.GetOrAddAsync(_entries, {|#0:delegate { return LoadManyAsync(new[] { _offset }, CancellationToken.None); }|}, token);"""),
            Fixture.Service("""ValueTask<KeyValuePair<int, Item?>[]> Get(CancellationToken token) => _cache.GetOrAddAsync(_entries, this, static (self, arg, ct) => { return self.LoadManyAsync(new[] { self._offset }, CancellationToken.None); }, token);"""),
            Verify.Diagnostic(0, "captures 'this' and allocates a delegate", "pass it as the argument and make the lambda static"));

    [Fact]
    public Task Method_group_on_a_possibly_null_target_gets_no_fix()
    {
        // Converting `repo!.LoadAsync` throws on a null repo at the call; as state, a null would surface only on a miss.
        var source = Fixture.Service("""ValueTask<Item?> Get(Repo? repo, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:repo!.LoadAsync|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "is the instance method group 'LoadAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));
    }

    [Fact]
    public Task Extension_method_group_on_a_possibly_null_target_is_fixed() =>
        // An extension method takes a null receiver whether it is converted or called, so nothing changes.
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(Repo? repo, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:repo.LoadOrDefaultAsync|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(Repo? repo, CancellationToken token) => _cache.GetOrAddAsync("k", repo, static (repo, ct) => repo.LoadOrDefaultAsync(ct), token);"""),
            Verify.Diagnostic(0, "is the instance method group 'LoadOrDefaultAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Primary_constructor_parameter_assigned_in_parentheses_gets_no_fix()
    {
        var source = """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public void Swap(Repo other) => (repo) = other;

                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => repo.LoadAsync(c)|}, ct);
            }
            """;
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'repo' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Mutable_struct_changed_through_a_member_gets_no_fix()
    {
        var source = Fixture.Service("""
            struct Counter
            {
                public int Count;

                public int Next() => ++Count;
            }

            ValueTask<Item?> Get(CancellationToken token)
            {
                var counter = new Counter();
                var pending = _cache.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(counter.Count, ct)|}, token);
                counter.Count++;
                return pending;
            }

            ValueTask<Item?> Find(CancellationToken token)
            {
                var counter = new Counter();
                var pending = _cache.GetOrAddAsync("b", {|#1:ct => LoadStaticAsync(counter.Count, ct)|}, token);
                counter.Next();
                return pending;
            }
            """);
        return Verify.FixAsync(
            source,
            source,
            Verify.Diagnostic(0, "captures 'counter' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'counter' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Readonly_struct_read_through_its_methods_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(DateTimeOffset now, CancellationToken token)
                {
                    _ = now.AddDays(1);
                    return _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(now.Day, ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(DateTimeOffset now, CancellationToken token)
                {
                    _ = now.AddDays(1);
                    return _cache.GetOrAddAsync("k", now, static (now, ct) => LoadStaticAsync(now.Day, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'now' and allocates a closure", PassIt));

    [Fact]
    public Task Call_whose_state_overload_is_not_in_scope_gets_no_fix()
    {
        // `using static CacheExtensions` brings the closure overload but not StatefulCacheExtensions: the rewrite would not bind.
        var source = """
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using static UiPath.Caching.CacheExtensions;

            public class Item { }

            public class Service(UiPath.Caching.ICache cache)
            {
                private Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());

                public ValueTask<Item?> Get(CancellationToken token) => cache.GetOrAddAsync<Item>("k", {|#0:ct => LoadAsync(ct)|}, token);
            }
            """;
        return Verify.FixWholeFileAsync(source, source, null, Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Pointer_capture_gets_no_fix()
    {
        var source = Fixture.Service("""unsafe ValueTask<Item?> Get(int* id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(*id, ct)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Names_that_are_only_underscores_and_digits_stay_identifiers() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var _1 = 2;
                    return _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(_1, ct)|}, token);
                }

                ValueTask<Item?> Find(Repo _1, CancellationToken token) => _cache.GetOrAddAsync("k", {|#1:_1.LoadAsync|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var _1 = 2;
                    return _cache.GetOrAddAsync("k", (Self: this, Value1: _1), static (s, ct) => s.Self.LoadAsync(s.Value1, ct), token);
                }

                ValueTask<Item?> Find(Repo _1, CancellationToken token) => _cache.GetOrAddAsync("k", _1, static (target, ct) => target.LoadAsync(ct), token);
                """),
            Verify.Diagnostic(0, "captures 'this' and '_1' and allocates a closure", "pass them as state and make the lambda static"),
            Verify.Diagnostic(1, "is the instance method group 'LoadAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Contextual_keyword_names_keep_their_escape() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var @await = 1;
                    return _cache.GetOrAddAsync("k", {|#0:async ct => await LoadStaticAsync(@await, ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var @await = 1;
                    return _cache.GetOrAddAsync("k", @await, static async (@await, ct) => await LoadStaticAsync(@await, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'await' and allocates a closure", PassIt));

    [Fact]
    public Task State_overload_whose_type_parameters_are_reordered_gets_no_fix_for_spelled_type_arguments()
    {
        // `<TState, T, TArgument>` against the closure's `<T, TState>`: appending a type argument would shift the others.
        var cache = CacheImplementation.Abstract("UiPath.Caching.ICache", "ReorderedCache", """
                public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, T, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
                    where TState : notnull => default;
            """);
        var source = Fixture.Service("""
            ValueTask<KeyValuePair<int, Item?>[]> Get(ReorderedCache cache, CancellationToken token) => cache.GetOrAddAsync<Item, int>(_entries, {|#0:(ids, ct) => LoadManyAsync(ids, ct)|}, _policy, token);
            """) + cache;
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and allocates a delegate", "pass it as the argument and make the lambda static"));
    }

    [Fact]
    public async Task Generator_inside_an_expression_tree_gets_no_fix()
    {
        // An expression tree takes neither a static lambda nor a tuple literal (CS8143).
        var source = Fixture.Service("""
            System.Linq.Expressions.Expression<Func<ValueTask<Item?>>> Get() => () => _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, CancellationToken.None);
            """);
        await Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Mutable_struct_read_through_a_getter_that_is_not_readonly_gets_no_fix()
    {
        var source = Fixture.Service("""
            struct Ticker
            {
                public int Count;

                public int Next => ++Count;
            }

            ValueTask<Item?> Get(CancellationToken token)
            {
                var ticker = new Ticker();
                return _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(ticker.Next, ct)|}, token);
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'ticker' and allocates a closure", PassIt));
    }

    [Fact]
    public Task State_overload_with_a_constrained_state_is_not_used() =>
        // The class's own `where TState : struct` overload would not take `this`: only the interface's remains, which a class does not see.
        Verify.FixAsync(ConstrainedCacheSource("where TState : struct", fixedSource: false), ConstrainedCacheSource("where TState : struct", fixedSource: false), Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));

    [Fact]
    public Task State_overload_without_the_constraint_is_used() =>
        // The control for the test above: the same class, the constraint removed, and the fix moves the call to it.
        Verify.FixAsync(ConstrainedCacheSource(string.Empty, fixedSource: false), ConstrainedCacheSource(string.Empty, fixedSource: true), Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));

    [Fact]
    public Task State_overload_with_another_return_type_is_not_used()
    {
        var source = Fixture.Service("""ValueTask<Item?> Get(RegionCache<Item> cache, CancellationToken token) => cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, token);""")
            + CacheImplementation.Abstract("UiPath.Caching.ICache`1", "RegionCache<T>", """
                    public string GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default) => "";
                """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Primary_constructor_parameter_of_a_mutable_struct_gets_no_fix()
    {
        // Any member may change it: `Tick` here, where the lambda's copy in the state would not see it.
        var source = """
            public class Item { }

            public struct Counter
            {
                public int Count;
            }

            public class UserService(ICache<Item> cache, Counter counter)
            {
                public void Tick() => counter.Count++;

                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => LoadAsync(counter.Count, c)|}, ct);

                private static Task<Item?> LoadAsync(int count, CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }
            """;
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'counter' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Omitted_caller_argument_expression_gets_no_fix()
    {
        // ThrowIfNull records the text of its argument as ParamName: `s.Self._repo` would replace `_repo`.
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct =>
            {
                ArgumentNullException.ThrowIfNull(_repo);
                return LoadAsync(id, ct);
            }|}, token);
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", "pass them as state and make the lambda static"));
    }

    [Fact]
    public Task Value_of_a_type_parameter_that_may_be_a_mutable_struct_gets_no_fix()
    {
        // `TCounter : struct` or an unconstrained `T` may be a mutable struct: a copy in the state would take the mutation instead.
        var source = Fixture.Service("""
            interface ICounter
            {
                int Next();
            }

            ValueTask<Item?> Get<TCounter>(TCounter counter, CancellationToken token)
                where TCounter : struct, ICounter =>
                _cache.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(counter.Next(), ct)|}, token);

            ValueTask<Item?> Find<T>(T value, CancellationToken token) =>
                _cache.GetOrAddAsync("b", {|#1:ct => LoadStaticAsync(value!.GetHashCode(), ct)|}, token);
            """);
        return Verify.FixAsync(
            source,
            source,
            Verify.Diagnostic(0, "captures 'counter' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'value' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Primary_constructor_parameter_of_a_type_parameter_gets_no_fix()
    {
        var source = """
            public class Item { }

            public interface ICounter
            {
                int Next();
            }

            public class UserService<TCounter>(ICache<Item> cache, TCounter counter)
                where TCounter : struct, ICounter
            {
                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => LoadAsync(counter.Next(), c)|}, ct);

                private static Task<Item?> LoadAsync(int count, CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }
            """;
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'counter' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Fix_applies_wherever_the_call_stands() =>
        // A local's initializer, an awaited one, a lambda, a field and a property initializer: each binds the rewrite in place.
        Verify.FixAsync(
            Fixture.Service("""
                private static readonly ICache<Item> Shared = null!;

                private static readonly Func<int, ValueTask<Item?>> Find = id => Shared.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(id, ct)|}, CancellationToken.None);

                private static Func<int, ValueTask<Item?>> Lookup { get; } = id => Shared.GetOrAddAsync("b", {|#1:ct => LoadStaticAsync(id, ct)|}, CancellationToken.None);

                async Task<Item?> Awaited(int id, CancellationToken token)
                {
                    var item = await _cache.GetOrAddAsync("c", {|#2:ct => LoadStaticAsync(id, ct)|}, token);
                    return item;
                }

                ValueTask<Item?> Pending(int id, CancellationToken token)
                {
                    var pending = _cache.GetOrAddAsync("d", {|#3:ct => LoadStaticAsync(id, ct)|}, token);
                    return pending;
                }

                Func<ValueTask<Item?>> Deferred(int id, CancellationToken token)
                {
                    Func<ValueTask<Item?>> get = () => _cache.GetOrAddAsync("e", {|#4:ct => LoadStaticAsync(id, ct)|}, token);
                    return get;
                }
                """),
            Fixture.Service("""
                private static readonly ICache<Item> Shared = null!;

                private static readonly Func<int, ValueTask<Item?>> Find = id => Shared.GetOrAddAsync("a", id, static (id, ct) => LoadStaticAsync(id, ct), CancellationToken.None);

                private static Func<int, ValueTask<Item?>> Lookup { get; } = id => Shared.GetOrAddAsync("b", id, static (id, ct) => LoadStaticAsync(id, ct), CancellationToken.None);

                async Task<Item?> Awaited(int id, CancellationToken token)
                {
                    var item = await _cache.GetOrAddAsync("c", id, static (id, ct) => LoadStaticAsync(id, ct), token);
                    return item;
                }

                ValueTask<Item?> Pending(int id, CancellationToken token)
                {
                    var pending = _cache.GetOrAddAsync("d", id, static (id, ct) => LoadStaticAsync(id, ct), token);
                    return pending;
                }

                Func<ValueTask<Item?>> Deferred(int id, CancellationToken token)
                {
                    Func<ValueTask<Item?>> get = () => _cache.GetOrAddAsync("e", id, static (id, ct) => LoadStaticAsync(id, ct), token);
                    return get;
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(2, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(3, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(4, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Dynamically_bound_call_on_this_goes_through_the_state() =>
        Verify.FixAsync(
            Fixture.Service("""
                private readonly dynamic _dynamic = 1;

                private Task<Item?> LoadObjectAsync(object value, CancellationToken ct) => Task.FromResult<Item?>(new Item());

                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadObjectAsync(_dynamic, ct)|}, token);
                """),
            Fixture.Service("""
                private readonly dynamic _dynamic = 1;

                private Task<Item?> LoadObjectAsync(object value, CancellationToken ct) => Task.FromResult<Item?>(new Item());

                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadObjectAsync(self._dynamic, ct), token);
                """),
            Verify.Diagnostic(0, "captures 'this' and allocates a delegate", PassIt));

    [Fact]
    public Task Capture_whose_name_the_body_declares_again_gets_a_fresh_parameter() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        var id = 0;
                        return LoadStaticAsync(id, ct);
                    }

                    return LoadStaticAsync(id, ct);
                }|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", id, static (id2, ct) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        var id = 0;
                        return LoadStaticAsync(id, ct);
                    }

                    return LoadStaticAsync(id2, ct);
                }, token);
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Range_variable_used_in_the_generator_is_a_capture()
    {
        var source = Fixture.Service("""
            IEnumerable<ValueTask<Item?>> All(int[] ids, CancellationToken token) =>
                from id in ids
                select _cache.GetOrAddAsync(id, {|#0:ct => LoadStaticAsync(id, ct)|}, token);
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Method_group_and_local_function_on_a_state_overload_are_told_to_add_to_the_state()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", 1, {|#0:LoadAsync|}, token);

            ValueTask<Item?> Find(CancellationToken token)
            {
                Task<Item?> Load(int id, CancellationToken ct) => LoadAsync(id, ct);
                return _cache.GetOrAddAsync("k", 1, {|#1:Load|}, token);
            }
            """);
        return Verify.FixAsync(
            source,
            source,
            Verify.Diagnostic(0, "is the instance method group 'LoadAsync' and allocates a delegate", "add its target to the state and call it from a static lambda"),
            Verify.Diagnostic(1, "is the local function 'Load', which captures 'this', and allocates a delegate", "add it to the state and use a static lambda"));
    }

    [Fact]
    public Task Null_forgiving_use_of_a_primary_constructor_parameter_is_not_a_write() =>
        Verify.FixAsync(
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo? repo)
            {
                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => repo!.LoadAsync(c)|}, ct);
            }
            """,
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo? repo)
            {
                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", repo, static (repo, c) => repo!.LoadAsync(c), ct);
            }
            """,
            Verify.Diagnostic(0, "captures 'repo' and allocates a delegate", PassIt));

    [Fact]
    public Task Capture_named_item_keeps_the_element_name_Item() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int item, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(item, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int item, CancellationToken token) => _cache.GetOrAddAsync("k", (Self: this, Item: item), static (s, ct) => s.Self.LoadAsync(s.Item, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'item' and allocates a closure", "pass them as state and make the lambda static"));

    [Fact]
    public Task Capture_named_by_nameof_or_inference_gets_no_fix()
    {
        // As a tuple element `id` becomes `s.Id`, which compiles but changes what nameof and an anonymous type's member name say.
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("a", {|#0:ct => LoadAsync(nameof(id).Length + id, ct)|}, token);

            ValueTask<Item?> Find(int id, CancellationToken token) => _cache.GetOrAddAsync("b", {|#1:ct => LoadAsync(new { id }.ToString().Length, ct)|}, token);
            """);
        return Verify.FixAsync(
            source,
            source,
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", "pass them as state and make the lambda static"),
            Verify.Diagnostic(1, "captures 'this' and 'id' and allocates a closure", "pass them as state and make the lambda static"));
    }

    [Fact]
    public Task Capture_declared_in_another_file_of_a_partial_type_is_reported() =>
        // The parameter's offset in the first file falls inside the lambda's span in the second: only the tree tells them apart.
        Verify.FixFilesAsync(
            [PartialServiceDeclaration(), PartialServiceMember("{|#0:c =>", "|}")],
            [PartialServiceDeclaration(), PartialServiceMember("repo, static (repo, c) =>", string.Empty)],
            Verify.Diagnostic(0, "captures 'repo' and allocates a delegate", PassIt));

    private static string PartialServiceDeclaration() => """
        // The primary constructor is declared here; the lambda that captures its parameter is in the other file, at overlapping offsets.
        public class Item { }

        public class Repo
        {
            public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
        }

        public partial class UserService(ICache<Item> cache, Repo repo);
        """;

    private static string PartialServiceMember(string lambdaStart, string lambdaEnd) => $$"""
        public partial class UserService
        {
            public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {{lambdaStart}}
            {
                // This lambda is long enough to span, by offset alone, the parameter declared in the other file:
                // a declaration counts as inside the lambda only when it is in the same syntax tree as well.
                // Padding so the span is wide whatever the length of the usings above.
                // More padding.
                return repo.LoadAsync(c);
            }{{lambdaEnd}}, ct);
        }
        """;

    private static string ConstrainedCacheSource(string constraint, bool fixedSource) =>
        (fixedSource
            ? Fixture.Service("""ValueTask<Item?> Get(RegionCache<Item> cache, CancellationToken token) => cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);""")
            : Fixture.Service("""ValueTask<Item?> Get(RegionCache<Item> cache, CancellationToken token) => cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, token);"""))
        + CacheImplementation.Abstract("UiPath.Caching.ICache`1", "RegionCache<T>", $$"""
                public ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
                    {{constraint}} => default;
            """);
}
