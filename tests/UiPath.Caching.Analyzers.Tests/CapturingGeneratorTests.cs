using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

public class CapturingGeneratorTests
{
    private const string ThisDelegate = "captures 'this' and allocates a delegate";
    private const string PassIt = "pass it as state and make the lambda static";
    private const string PassThem = "pass them as state and make the lambda static";

    [Fact]
    public Task Lambda_capturing_this_passes_this_as_state() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Lambda_capturing_a_local_passes_the_local_and_keeps_its_name() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = Environment.TickCount;
                    return _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = Environment.TickCount;
                    return _cache.GetOrAddAsync("k", id, static (id, ct) => LoadStaticAsync(id, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Lambda_capturing_a_parameter_passes_the_parameter() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync(id, {|#0:c => LoadStaticAsync(id, c)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync(id, id, static (id, c) => LoadStaticAsync(id, c), token);"""),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Several_captures_become_a_named_tuple() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync(id, {|#0:ct => LoadAsync(id + name.Length, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync(id, (Self: this, Id: id, Name: name), static (s, ct) => s.Self.LoadAsync(s.Id + s.Name.Length, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this', 'id' and 'name' and allocates a closure", PassThem));

    [Fact]
    public Task Implicit_and_explicit_this_member_access_go_through_the_state() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => _offset > 0 ? this.LoadAsync(ct) : LoadStaticAsync(Offset, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self._offset > 0 ? self.LoadAsync(ct) : LoadStaticAsync(self.Offset, ct), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Async_block_lambda_keeps_its_body_and_async() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:async ct =>
                {
                    var item = await LoadAsync(ct);
                    return item;
                }|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static async (self, ct) =>
                {
                    var item = await self.LoadAsync(ct);
                    return item;
                }, token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Anonymous_method_becomes_a_static_lambda() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:delegate (CancellationToken ct) { return LoadAsync(ct); }|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => { return self.LoadAsync(ct); }, token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Instance_method_group_of_this_becomes_a_static_lambda_over_this() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:LoadAsync|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);"""),
            Verify.Diagnostic(0, "is the instance method group 'LoadAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Instance_method_group_of_a_field_passes_the_field() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:_repo.LoadAsync|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", _repo, static (repo, ct) => repo.LoadAsync(ct), token);"""),
            Verify.Diagnostic(0, "is the instance method group 'LoadAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Static_lambda_is_not_reported() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);"""));

    [Fact]
    public Task Lambda_that_captures_nothing_is_not_reported() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", ct => LoadStaticAsync(ct), token);"""));

    [Fact]
    public Task Static_method_group_is_not_reported() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", LoadStaticAsync, token);"""));

    [Fact]
    public Task Delegate_field_is_not_reported() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", _load, token);"""));

    [Fact]
    public Task Capturing_lambda_on_a_state_overload_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", this, {|#0:(self, ct) => self.LoadAsync(id, ct)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", "add it to the state and make the lambda static"));
    }

    [Fact]
    public Task Interpolated_key_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _cache.GetOrAddAsync($"{id}", {|#0:ct => LoadAsync(ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _cache.GetOrAddAsync($"{id}", this, static (self, ct) => self.LoadAsync(ct), token);
                }
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task A_comment_before_the_generator_leads_the_state_and_is_not_doubled() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", /* load */ {|#0:ct => LoadAsync(ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", /* load */ this, static (self, ct) => self.LoadAsync(ct), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task A_discarded_token_stays_a_discard() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:_ => LoadAsync(token)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, token, static (self, token, _) => self.LoadAsync(token), token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'token' and allocates a closure", PassThem));

    [Fact]
    public Task Untyped_interpolated_key_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _untyped.GetOrAddAsync<Item>($"{id}", {|#0:ct => LoadAsync(ct)|}, _policy, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _untyped.GetOrAddAsync<Item, Service>($"{id}", this, static (self, ct) => self.LoadAsync(ct), _policy, token);
                }
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Concrete_cache_span_key_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    Span<char> key = stackalloc char[1];
                    key[0] = 'k';
                    return _concrete.GetOrAddAsync(key, {|#0:ct => LoadAsync(ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    Span<char> key = stackalloc char[1];
                    key[0] = 'k';
                    return _concrete.GetOrAddAsync(key, this, static (self, ct) => self.LoadAsync(ct), token);
                }
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Interpolated_key_generated_expiration_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _cache.GetOrAddWithExpirationAsync($"{id}", {|#0:ct => GenerateAsync(ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token)
                {
                    var id = "k";
                    return _cache.GetOrAddWithExpirationAsync($"{id}", this, static (self, ct) => self.GenerateAsync(ct), token);
                }
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Untyped_cache_keeps_expiration_and_policy_and_infers_T() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _untyped.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, TimeSpan.FromMinutes(5), _policy, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _untyped.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), TimeSpan.FromMinutes(5), _policy, token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Untyped_cache_extension_without_policy_moves_to_the_state_extension() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _untyped.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, DateTimeOffset.MaxValue, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _untyped.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), DateTimeOffset.MaxValue, token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Typed_cache_keeps_expiration() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, TimeSpan.FromMinutes(5), token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), TimeSpan.FromMinutes(5), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Named_generator_argument_gets_a_named_state() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", generator: {|#0:ct => LoadAsync(ct)|}, token: token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", state: this, generator: static (self, ct) => self.LoadAsync(ct), token: token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Arguments_on_their_own_lines_stay_there() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync(
                    "k",
                    {|#0:ct => LoadAsync(ct)|},
                    token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync(
                    "k",
                    this,
                    static (self, ct) => self.LoadAsync(ct),
                    token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Typed_hash_cache_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _hash.GetOrAddAsync("k", {|#0:ct => LoadAllInstanceAsync(ct)|}, TimeSpan.FromMinutes(1), token);"""),
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _hash.GetOrAddAsync("k", this, static (self, ct) => self.LoadAllInstanceAsync(ct), TimeSpan.FromMinutes(1), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Untyped_hash_cache_with_spelled_type_arguments_gets_the_state_type_appended() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(int id, CancellationToken token) => _untypedHash.GetOrAddAsync<Item>("k", {|#0:ct => LoadAllInstanceAsync(id, ct)|}, DateTimeOffset.MaxValue, HashCacheSetOption.KeyReplace, _policy, token);"""),
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(int id, CancellationToken token) => _untypedHash.GetOrAddAsync<Item, Service, int>("k", this, id, static (self, id, ct) => self.LoadAllInstanceAsync(id, ct), DateTimeOffset.MaxValue, HashCacheSetOption.KeyReplace, _policy, token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Untyped_hash_cache_without_policy_moves_to_the_state_extension() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _untypedHash.GetOrAddAsync<Item>("k", {|#0:LoadAllInstanceAsync|}, token);"""),
            Fixture.Service("""ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _untypedHash.GetOrAddAsync<Item, Service>("k", this, static (self, ct) => self.LoadAllInstanceAsync(ct), token);"""),
            Verify.Diagnostic(0, "is the instance method group 'LoadAllInstanceAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Generated_expiration_generator_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddWithExpirationAsync("k", {|#0:ct => GenerateAsync(ct)|}, token);

                ValueTask<Item?> Find(int id, CancellationToken token) => _untyped.GetOrAddWithExpirationAsync<Item>("k", {|#1:ct => GenerateAsync(id, ct)|}, _policy, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddWithExpirationAsync("k", this, static (self, ct) => self.GenerateAsync(ct), token);

                ValueTask<Item?> Find(int id, CancellationToken token) => _untyped.GetOrAddWithExpirationAsync<Item, Service, int>("k", this, id, static (self, id, ct) => self.GenerateAsync(id, ct), _policy, token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt),
            Verify.Diagnostic(1, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Batch_generator_passes_the_capture_as_the_argument() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<KeyValuePair<int, Item?>[]> Get(CancellationToken token) => _cache.GetOrAddAsync(_entries, {|#0:(ids, ct) => LoadManyAsync(ids, ct)|}, token);

                ValueTask<KeyValuePair<int, Item?>[]> All(CancellationToken token) => _cache.GetOrAddAsync(_entries, {|#1:LoadManyAsync|}, TimeSpan.FromMinutes(1), token);

                ValueTask<KeyValuePair<int, Item?>[]> Untyped(int extra, CancellationToken token) => _untyped.GetOrAddAsync<Item, int>(_entries, {|#2:(ids, ct) => LoadManyAsync(ids, ct, extra)|}, _policy, token);
                """),
            Fixture.Service("""
                ValueTask<KeyValuePair<int, Item?>[]> Get(CancellationToken token) => _cache.GetOrAddAsync(_entries, this, static (self, ids, ct) => self.LoadManyAsync(ids, ct), token);

                ValueTask<KeyValuePair<int, Item?>[]> All(CancellationToken token) => _cache.GetOrAddAsync(_entries, this, static (self, ids, ct) => self.LoadManyAsync(ids, ct), TimeSpan.FromMinutes(1), token);

                ValueTask<KeyValuePair<int, Item?>[]> Untyped(int extra, CancellationToken token) => _untyped.GetOrAddAsync<Item, int, (Service Self, int Extra)>(_entries, (Self: this, Extra: extra), static (s, ids, ct) => s.Self.LoadManyAsync(ids, ct, s.Extra), _policy, token);
                """),
            Verify.Diagnostic(0, ThisDelegate, "pass it as the argument and make the lambda static"),
            Verify.Diagnostic(1, "is the instance method group 'LoadManyAsync' and allocates a delegate", "pass its target as the argument and call it from a static lambda"),
            Verify.Diagnostic(2, "captures 'this' and 'extra' and allocates a closure", "pass them as the argument and make the lambda static"));

    [Fact]
    public Task Capturing_batch_generator_with_an_argument_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""ValueTask<KeyValuePair<int, Item?>[]> Get(int extra, CancellationToken token) => _cache.GetOrAddAsync(_entries, this, {|#0:(self, ids, ct) => self.LoadManyAsync(ids, ct, extra)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'extra' and allocates a closure", "add it to the argument and make the lambda static"));
    }

    [Fact]
    public Task Capture_through_a_local_function_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token)
            {
                Task<Item?> Load(CancellationToken ct) => LoadAsync(id, ct);
                return _cache.GetOrAddAsync("k", {|#0:ct => Load(ct)|}, token);
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));
    }

    [Fact]
    public Task Capturing_local_function_as_a_method_group_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token)
            {
                Task<Item?> Load(CancellationToken ct) => LoadStaticAsync(id, ct);
                return _cache.GetOrAddAsync("k", {|#0:Load|}, token);
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "is the local function 'Load', which captures 'id', and allocates a closure", "pass it as state to a static lambda"));
    }

    [Fact]
    public Task Local_function_that_captures_nothing_as_a_method_group_is_not_reported() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                static Task<Item?> Load(CancellationToken ct) => LoadStaticAsync(ct);
                Task<Item?> Plain(CancellationToken ct) => LoadStaticAsync(ct);
                return token.IsCancellationRequested ? _cache.GetOrAddAsync("k", Load, token) : _cache.GetOrAddAsync("k", Plain, token);
            }
            """));

    [Fact]
    public Task Static_local_function_is_not_a_capture() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                static Task<Item?> Load(CancellationToken ct) => LoadStaticAsync(ct);
                return _cache.GetOrAddAsync("k", ct => Load(ct), token);
            }
            """));

    [Fact]
    public Task Names_used_only_in_nameof_are_not_captures() =>
        Verify.NoDiagnosticAsync(Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", ct => LoadStaticAsync(nameof(id).Length + nameof(_offset).Length + nameof(LoadAsync).Length, ct), token);
            """));

    [Fact]
    public Task Concrete_cache_class_with_its_own_state_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _concrete.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, TimeSpan.FromMinutes(1), token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _concrete.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), TimeSpan.FromMinutes(1), token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task NullCache_with_its_own_state_overload_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => NullCache.Instance.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, _policy, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => NullCache.Instance.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), _policy, token);"""),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Concrete_cache_class_whose_state_overload_is_only_on_the_interface_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(OwnCache cache, CancellationToken token) => cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, token);

            public sealed class OwnCache : ICache<Item>
            {
                public ValueTask<Item?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<Item?>> generator, CancellationToken token = default) => throw null!;
                string ICache<Item>.Name => "own";
                ValueTask<Item?> ICache<Item>.GetAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<CacheKey, Item?>[]> ICache<Item>.GetAsync(CacheKey[] cacheKeys, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<Item?>> generator, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<Item?>> generator, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<Item?>> generator, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<Item?>> generator, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<Item?>> generator, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<Item>>> generator, CancellationToken token) => throw null!;
                ValueTask<Item?> ICache<Item>.GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<Item>>> generator, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<KeyValuePair<TState, Item?>[]> ICache<Item>.GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.RemoveAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.RemoveAsync(CacheKey[] cacheKeys, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(CacheKey cacheKey, Item? value, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(CacheKey cacheKey, Item? value, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(CacheKey cacheKey, Item? value, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(KeyValuePair<CacheKey, Item?>[] keyValues, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(KeyValuePair<CacheKey, Item?>[] keyValues, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.SetAsync(KeyValuePair<CacheKey, Item?>[] keyValues, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.TryAddAsync(CacheKey cacheKey, Item? value, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.TryAddAsync(CacheKey cacheKey, Item? value, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.TryAddAsync(CacheKey cacheKey, Item? value, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.RefreshAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) => throw null!;
                ValueTask<bool> ICache<Item>.ContainsAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
                ValueTask<TimeSpan?> ICache<Item>.TimeToLiveAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
                ValueTask<DateTimeOffset?> ICache<Item>.ExpireTimeAsync(CacheKey cacheKey, CancellationToken token) => throw null!;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, ThisDelegate, PassIt));
    }

    [Fact]
    public Task Nested_calls_are_fixed_outer_then_inner_under_fix_all() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("outer", {|#0:async ct => await _cache.GetOrAddAsync("inner", {|#1:c => LoadAsync(c)|}, ct)|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("outer", this, static async (self, ct) => await self._cache.GetOrAddAsync("inner", self, static (self, c) => self.LoadAsync(c), ct), token);
                """),
            numberOfFixAllIterations: 2,
            Verify.Diagnostic(0, ThisDelegate, PassIt),
            Verify.Diagnostic(1, ThisDelegate, PassIt));

    [Fact]
    public Task Overload_shapes_compile_under_CSharp_12() =>
        // The analyzer tests compile with the .NET 8 SDK's compiler, C# 12, which does not read OverloadResolutionPriority.
        Verify.NoDiagnosticAsync(Fixture.Service("""
            async Task Shapes(Func<CancellationToken, Task<IDictionary<string, Item?>>> hash, Func<CancellationToken, Task<GeneratedValue<Item>>> generated, Func<int[], CancellationToken, Task<KeyValuePair<int, Item?>[]>> batch, CancellationToken token)
            {
                await _untypedHash.GetOrAddAsync<Item>("k", _ => throw new InvalidOperationException(), null, token);
                await _untypedHash.GetOrAddAsync("k", hash, DateTimeOffset.MaxValue, HashCacheSetOption.KeyReplace, null);
                await _hash.GetOrAddAsync("k", _ => throw new InvalidOperationException(), token);
                await _hash.GetOrAddAsync("k", async _ => default!, TimeSpan.FromMinutes(1), token);
                await _hash.GetOrAddAsync("k", hash, default);
                await _untyped.GetOrAddWithExpirationAsync<Item>("k", _ => throw new InvalidOperationException(), null, token);
                await _untyped.GetOrAddWithExpirationAsync("k", generated, null);
                await _cache.GetOrAddWithExpirationAsync("k", _ => throw new InvalidOperationException(), token);
                await _cache.GetOrAddWithExpirationAsync("k", async _ => default, token);
                await _cache.GetOrAddWithExpirationAsync("k", generated, default);
                await _untyped.GetOrAddAsync<Item, int>(_entries, (_, _) => throw new InvalidOperationException(), null, token);
                await _untyped.GetOrAddAsync(_entries, batch, TimeSpan.FromMinutes(1), null);
                await _cache.GetOrAddAsync(_entries, (_, _) => throw new InvalidOperationException(), token);
                await _cache.GetOrAddAsync(_entries, async (_, _) => default!, TimeSpan.FromMinutes(1), token);
                await _cache.GetOrAddAsync(_entries, batch, default);
            }
            """));

    [Fact]
    public Task Capture_written_after_the_call_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                var id = 1;
                var pending = _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                id++;
                return pending;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Capture_written_inside_the_lambda_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                var calls = 0;
                return _cache.GetOrAddAsync("k", {|#0:ct => { calls++; return LoadStaticAsync(calls, ct); }|}, token);
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'calls' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Base_access_is_reported_without_a_fix()
    {
        var source = Fixture.Service("""ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => base.ToString() is null ? LoadAsync(ct) : LoadStaticAsync(ct)|}, token);""");
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, ThisDelegate, PassIt));
    }

    [Fact]
    public Task Struct_this_cannot_be_captured_and_is_not_reported() =>
        // A lambda in a struct cannot use 'this' (CS1673), so there is no capture to report or pass.
        Verify.NoDiagnosticAsync(
            """
            public class Item { }

            public struct Holder
            {
                private readonly ICache<Item> _cache;
                private readonly int _offset;

                public Holder(ICache<Item> cache) => (_cache, _offset) = (cache, 1);

                private static Task<Item?> LoadStaticAsync(int id, CancellationToken ct) => Task.FromResult<Item?>(new Item());

                public ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", ct => LoadStaticAsync({|CS1673:_offset|}, ct), token);
            }
            """);

    [Fact]
    public Task Span_cannot_be_captured_and_is_not_reported() =>
        // A lambda cannot capture a span (CS8175), so there is no capture to report or pass.
        Verify.NoDiagnosticAsync(Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                Span<char> key = stackalloc char[1];
                return _cache.GetOrAddAsync("k", ct => LoadStaticAsync({|CS8175:key|}.Length, ct), token);
            }
            """));

    [Fact]
    public Task Primary_constructor_parameter_is_passed_as_state() =>
        Verify.FixAsync(
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(int id, CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public ValueTask<Item?> GetAsync(int id, CancellationToken ct) => cache.GetOrAddAsync(id, {|#0:c => repo.LoadAsync(id, c)|}, TimeSpan.FromMinutes(5), ct);
            }
            """,
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(int id, CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public ValueTask<Item?> GetAsync(int id, CancellationToken ct) => cache.GetOrAddAsync(id, repo, id, static (repo, id, c) => repo.LoadAsync(id, c), TimeSpan.FromMinutes(5), ct);
            }
            """,
            Verify.Diagnostic(0, "captures 'repo' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Lone_primary_constructor_parameter_is_the_state() =>
        Verify.FixAsync(
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => repo.LoadAsync(c)|}, ct);
            }
            """,
            """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", repo, static (repo, c) => repo.LoadAsync(c), ct);
            }
            """,
            Verify.Diagnostic(0, "captures 'repo' and allocates a delegate", PassIt));

    [Fact]
    public Task Primary_constructor_parameter_assigned_elsewhere_gets_no_fix()
    {
        var source = """
            public class Item { }

            public class Repo
            {
                public Task<Item?> LoadAsync(CancellationToken ct) => Task.FromResult<Item?>(new Item());
            }

            public class UserService(ICache<Item> cache, Repo repo)
            {
                public void Swap(Repo other) => repo = other;

                public ValueTask<Item?> GetAsync(CancellationToken ct) => cache.GetOrAddAsync("k", {|#0:c => repo.LoadAsync(c)|}, ct);
            }
            """;
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'repo' and allocates a delegate", PassIt));
    }

    [Fact]
    public Task Fix_all_fixes_every_call() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:ct => LoadAsync(ct)|}, token);

                ValueTask<Item?> Find(int id, CancellationToken token) => _cache.GetOrAddAsync(id, {|#1:ct => LoadAsync(id, ct)|}, token);

                ValueTask<Item?> Load(CancellationToken token) => _cache.GetOrAddAsync("k", {|#2:LoadAsync|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);

                ValueTask<Item?> Find(int id, CancellationToken token) => _cache.GetOrAddAsync(id, this, id, static (self, id, ct) => self.LoadAsync(id, ct), token);

                ValueTask<Item?> Load(CancellationToken token) => _cache.GetOrAddAsync("k", this, static (self, ct) => self.LoadAsync(ct), token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt),
            Verify.Diagnostic(1, "captures 'this' and 'id' and allocates a closure", PassThem),
            Verify.Diagnostic(2, "is the instance method group 'LoadAsync' and allocates a delegate", "pass its target as state and call it from a static lambda"));

    [Fact]
    public Task Span_key_cache_of_T_call_passes_this_as_state() =>
        Verify.FixAsync(
            Fixture.Service("""
                private readonly ISpanKeyCache<Item> _spanCache = null!;
                private readonly char[] _key = ['k'];

                ValueTask<Item?> Get(CancellationToken token) => _spanCache.GetOrAddAsync(_key.AsSpan(), {|#0:ct => LoadAsync(ct)|}, token);
                """),
            Fixture.Service("""
                private readonly ISpanKeyCache<Item> _spanCache = null!;
                private readonly char[] _key = ['k'];

                ValueTask<Item?> Get(CancellationToken token) => _spanCache.GetOrAddAsync(_key.AsSpan(), this, static (self, ct) => self.LoadAsync(ct), token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Span_key_cache_call_passes_the_captures_as_state() =>
        Verify.FixAsync(
            Fixture.Service("""
                private readonly ISpanKeyCache _spanUntyped = null!;
                private readonly char[] _key = ['k'];

                ValueTask<Item?> Get(int id, CancellationToken token) => _spanUntyped.GetOrAddAsync<Item>(_key.AsSpan(), {|#0:ct => LoadAsync(id, ct)|}, TimeSpan.FromMinutes(1), _policy, token);
                """),
            Fixture.Service("""
                private readonly ISpanKeyCache _spanUntyped = null!;
                private readonly char[] _key = ['k'];

                ValueTask<Item?> Get(int id, CancellationToken token) => _spanUntyped.GetOrAddAsync<Item, (Service Self, int Id)>(_key.AsSpan(), (Self: this, Id: id), static (s, ct) => s.Self.LoadAsync(s.Id, ct), TimeSpan.FromMinutes(1), _policy, token);
                """),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Span_key_hash_cache_of_T_call_passes_this_as_state() =>
        Verify.FixAsync(
            Fixture.Service("""
                private readonly ISpanKeyHashCache<Item> _spanHash = null!;
                private readonly char[] _key = ['k'];

                ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _spanHash.GetOrAddAsync(_key.AsSpan(), {|#0:ct => LoadAllInstanceAsync(ct)|}, token);
                """),
            Fixture.Service("""
                private readonly ISpanKeyHashCache<Item> _spanHash = null!;
                private readonly char[] _key = ['k'];

                ValueTask<IDictionary<string, Item?>> Get(CancellationToken token) => _spanHash.GetOrAddAsync(_key.AsSpan(), this, static (self, ct) => self.LoadAllInstanceAsync(ct), token);
                """),
            Verify.Diagnostic(0, ThisDelegate, PassIt));

    [Fact]
    public Task Span_key_hash_cache_call_passes_the_captures_as_state() =>
        Verify.FixAsync(
            Fixture.Service("""
                private readonly ISpanKeyHashCache _spanUntypedHash = null!;
                private readonly char[] _key = ['k'];

                ValueTask<IDictionary<string, Item?>> Get(int id, CancellationToken token) => _spanUntypedHash.GetOrAddAsync<Item>(_key.AsSpan(), {|#0:ct => LoadAllInstanceAsync(id, ct)|}, _policy, token);
                """),
            Fixture.Service("""
                private readonly ISpanKeyHashCache _spanUntypedHash = null!;
                private readonly char[] _key = ['k'];

                ValueTask<IDictionary<string, Item?>> Get(int id, CancellationToken token) => _spanUntypedHash.GetOrAddAsync<Item, (Service Self, int Id)>(_key.AsSpan(), (Self: this, Id: id), static (s, ct) => s.Self.LoadAllInstanceAsync(s.Id, ct), _policy, token);
                """),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Two_captures_become_owner_and_argument() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync(id, {|#0:ct => LoadStaticAsync(id + name.Length, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync(id, id, name, static (id, name, ct) => LoadStaticAsync(id + name.Length, ct), token);"""),
            Verify.Diagnostic(0, "captures 'id' and 'name' and allocates a closure", PassThem));

    [Fact]
    public Task This_and_one_local_become_self_and_argument() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync(id, {|#0:ct => LoadAsync(_offset + id, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync(id, this, id, static (self, id, ct) => self.LoadAsync(self._offset + id, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task One_capture_becomes_the_state() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(string name, CancellationToken token) => _untyped.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(name.Length, ct)|}, _policy, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(string name, CancellationToken token) => _untyped.GetOrAddAsync("k", name, static (name, ct) => LoadStaticAsync(name.Length, ct), _policy, token);"""),
            Verify.Diagnostic(0, "captures 'name' and allocates a closure", PassIt));

    [Fact]
    public Task Three_captures_become_a_named_tuple() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, int extra, CancellationToken token) => _cache.GetOrAddAsync(id, {|#0:ct => LoadStaticAsync(id + name.Length + extra, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, int extra, CancellationToken token) => _cache.GetOrAddAsync(id, (Id: id, Name: name, Extra: extra), static (s, ct) => LoadStaticAsync(s.Id + s.Name.Length + s.Extra, ct), token);"""),
            Verify.Diagnostic(0, "captures 'id', 'name' and 'extra' and allocates a closure", PassThem));

    [Fact]
    public Task No_fix_emits_ItemN() =>
        // Verify.FixAsync fails on any ItemN in a fixed source; four captures are the widest tuple the fix writes here.
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int a, int b, int c, CancellationToken token) => _cache.GetOrAddAsync(a, {|#0:ct => LoadAsync(a + b + c, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int a, int b, int c, CancellationToken token) => _cache.GetOrAddAsync(a, (Self: this, A: a, B: b, C: c), static (s, ct) => s.Self.LoadAsync(s.A + s.B + s.C, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this', 'a', 'b' and 'c' and allocates a closure", PassThem));

    [Fact]
    public Task Reassigned_capture_is_snapshotted() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    var id0 = 7;
                    id += id0;
                    return _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    var id0 = 7;
                    id += id0;
                    var id1 = id;
                    return _cache.GetOrAddAsync("k", id1, static (id, ct) => LoadStaticAsync(id, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Reassigned_capture_beside_this_is_snapshotted_for_owner_and_argument() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    return _cache.GetOrAddAsync(id, {|#0:ct => LoadAsync(id, ct)|}, token);
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    var id0 = id;
                    return _cache.GetOrAddAsync(id, this, id0, static (self, id, ct) => self.LoadAsync(id, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Capture_reassigned_after_the_call_is_refused()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, string name, CancellationToken token)
            {
                id++;
                var pending = _cache.GetOrAddAsync(id, {|#0:ct => LoadStaticAsync(id + name.Length, ct)|}, token);
                name = "other";
                return pending;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and 'name' and allocates a closure", PassThem));
    }

    [Fact]
    public Task Interpolated_key_is_kept_as_is() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync($"k:{id}:{name}", {|#0:ct => LoadStaticAsync(id + name.Length, ct)|}, token);"""),
            Fixture.Service("""ValueTask<Item?> Get(int id, string name, CancellationToken token) => _cache.GetOrAddAsync($"k:{id}:{name}", id, name, static (id, name, ct) => LoadStaticAsync(id + name.Length, ct), token);"""),
            Verify.Diagnostic(0, "captures 'id' and 'name' and allocates a closure", PassThem));

    [Fact]
    public Task Result_call_is_fixed_with_the_owner_and_argument_overload() =>
        Verify.FixAsync(
            Fixture.Service("""ValueTask<CacheResult<Item>> Get(int id, CancellationToken token) => _cache.GetOrAddResultAsync(id, {|#0:ct => LoadAsync(id, ct)|}, token);"""),
            Fixture.Service("""ValueTask<CacheResult<Item>> Get(int id, CancellationToken token) => _cache.GetOrAddResultAsync(id, this, id, static (self, id, ct) => self.LoadAsync(id, ct), token);"""),
            Verify.Diagnostic(0, "captures 'this' and 'id' and allocates a closure", PassThem));

    [Fact]
    public Task Fix_all_gives_each_snapshot_its_own_name() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    var a = _cache.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                    var b = _cache.GetOrAddAsync("b", {|#1:ct => LoadStaticAsync(id, ct)|}, token);
                    return a;
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    var id0 = id;
                    var a = _cache.GetOrAddAsync("a", id0, static (id, ct) => LoadStaticAsync(id, ct), token);
                    var id1 = id;
                    var b = _cache.GetOrAddAsync("b", id1, static (id, ct) => LoadStaticAsync(id, ct), token);
                    return a;
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Async_block_lambda_with_ConfigureAwait_is_fixed() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:async ct =>
                {
                    var item = await LoadStaticAsync(id, ct).ConfigureAwait(false);
                    return item;
                }|}, token);
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", id, static async (id, ct) =>
                {
                    var item = await LoadStaticAsync(id, ct).ConfigureAwait(false);
                    return item;
                }, token);
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Capture_reassigned_inside_an_async_lambda_is_refused()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token) => _cache.GetOrAddAsync("k", {|#0:async ct =>
            {
                id++;
                return await LoadStaticAsync(id, ct).ConfigureAwait(false);
            }|}, token);
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Capture_written_in_a_loop_around_the_call_is_refused()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(CancellationToken token)
            {
                ValueTask<Item?> last = default;
                for (var i = 0; i < 3; i++)
                {
                    last = _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(i, ct)|}, token);
                }

                return last;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'i' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Reassigned_capture_with_the_call_outside_a_block_is_refused()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token)
            {
                id++;
                if (id > 0) return _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                return default;
            }

            ValueTask<Item?> Find(int id, CancellationToken token) => id++ > 0 ? _cache.GetOrAddAsync("k", {|#1:ct => LoadStaticAsync(id, ct)|}, token) : default;
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt), Verify.Diagnostic(1, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Fix_all_numbers_snapshots_across_different_methods() =>
        Verify.FixAsync(
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    var a = _cache.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                    var b = _cache.GetOrAddResultAsync("b", {|#1:ct => LoadStaticAsync(id, ct)|}, token);
                    return a;
                }
                """),
            Fixture.Service("""
                ValueTask<Item?> Get(int id, CancellationToken token)
                {
                    id++;
                    var id0 = id;
                    var a = _cache.GetOrAddAsync("a", id0, static (id, ct) => LoadStaticAsync(id, ct), token);
                    var id1 = id;
                    var b = _cache.GetOrAddResultAsync("b", id1, static (id, ct) => LoadStaticAsync(id, ct), token);
                    return a;
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Capture_aliased_by_ref_is_refused()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, CancellationToken token)
            {
                id++;
                ref int alias = ref id;
                var pending = _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                alias = 5;
                return pending;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }

    [Fact]
    public Task Fix_all_keeps_snapshot_names_distinct_when_one_is_taken() =>
        Verify.FixAsync(
            Fixture.Service("""
                void Fire(int id, CancellationToken token)
                {
                    var id0 = 0;
                    id = id0 + 1;
                    _ = _cache.GetOrAddAsync("a", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                    _ = _cache.GetOrAddAsync("b", {|#1:ct => LoadStaticAsync(id, ct)|}, token);
                }
                """),
            Fixture.Service("""
                void Fire(int id, CancellationToken token)
                {
                    var id0 = 0;
                    id = id0 + 1;
                    var id2 = id;
                    _ = _cache.GetOrAddAsync("a", id2, static (id, ct) => LoadStaticAsync(id, ct), token);
                    var id1 = id;
                    _ = _cache.GetOrAddAsync("b", id1, static (id, ct) => LoadStaticAsync(id, ct), token);
                }
                """),
            Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt),
            Verify.Diagnostic(1, "captures 'id' and allocates a closure", PassIt));

    [Fact]
    public Task Snapshot_under_a_conditional_operator_is_refused()
    {
        var source = Fixture.Service("""
            Task<Item?> LoadNameAsync(string name, CancellationToken ct) => Task.FromResult<Item?>(new Item());

            async Task<bool> Get(string? name, CancellationToken token)
            {
                name = name?.Trim();
                return name is not null && await _cache.GetOrAddAsync("k", {|#0:ct => LoadNameAsync(name, ct)|}, token) is not null;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'this' and 'name' and allocates a closure", PassThem));
    }

    [Fact]
    public Task Snapshot_is_refused_when_the_method_has_a_goto_case()
    {
        var source = Fixture.Service("""
            ValueTask<Item?> Get(int id, int n, CancellationToken token)
            {
                ValueTask<Item?> pending = default;
                switch (n)
                {
                    case 1:
                        id++;
                        goto case 2;
                    case 2:
                        pending = _cache.GetOrAddAsync("k", {|#0:ct => LoadStaticAsync(id, ct)|}, token);
                        if (n > 5)
                        {
                            goto case 1;
                        }

                        break;
                }

                return pending;
            }
            """);
        return Verify.FixAsync(source, source, Verify.Diagnostic(0, "captures 'id' and allocates a closure", PassIt));
    }
}
