using System.Reflection;
using Moq;

namespace UiPath.Caching.Tests;

/// <summary>The span forms of <c>TryAddAsync</c>, <c>RefreshAsync</c>, <c>RemoveFieldsAsync</c> and <c>SetMetadataAsync</c> build the <see cref="CacheKey"/> and call the <see cref="CacheKey"/> member, so a proxy answers them from the setup it already has.</summary>
public class SpanKeyDefaultWriteTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    private static readonly HashCacheEntryOptions Options = new(TimeToLive: Ttl);
    private static readonly string[] Fields = ["f"];
    private static readonly IDictionary<string, string?> Metadata = new Dictionary<string, string?> { ["m"] = "1" };
    private static readonly CacheKey Expected = new("user:42");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_substitute_cache_gets_each_span_write_through_its_CacheKey_member()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();

        await cache.TryAddAsync<string>($"{Key()}", "v", null, Ct);
        await cache.TryAddAsync<string>($"{Key()}", "v", Ttl, null, Ct);
        await cache.TryAddAsync<string>($"{Key()}", "v", Deadline, null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", Ttl, null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", Deadline, null, Ct);
        await typed.TryAddAsync($"{Key()}", "v", Ct);
        await typed.TryAddAsync($"{Key()}", "v", Ttl, Ct);
        await typed.TryAddAsync($"{Key()}", "v", Deadline, Ct);
        await typed.RefreshAsync($"{Key()}", Ct);
        await typed.RefreshAsync($"{Key()}", Ttl, Ct);
        await typed.RefreshAsync($"{Key()}", Deadline, Ct);

        await cache.Received(1).TryAddAsync<string>(Expected, "v", null, Ct);
        await cache.Received(1).TryAddAsync<string>(Expected, "v", Ttl, null, Ct);
        await cache.Received(1).TryAddAsync<string>(Expected, "v", Deadline, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, Ttl, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, Deadline, null, Ct);
        await typed.Received(1).TryAddAsync(Expected, "v", Ct);
        await typed.Received(1).TryAddAsync(Expected, "v", Ttl, Ct);
        await typed.Received(1).TryAddAsync(Expected, "v", Deadline, Ct);
        await typed.Received(1).RefreshAsync(Expected, Ct);
        await typed.Received(1).RefreshAsync(Expected, Ttl, Ct);
        await typed.Received(1).RefreshAsync(Expected, Deadline, Ct);
    }

    [Fact]
    public async Task A_substitute_hash_cache_gets_each_span_write_through_its_CacheKey_member()
    {
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();

        await cache.RefreshAsync<string>($"{Key()}", null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", Ttl, null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", Deadline, null, Ct);
        await cache.RefreshAsync<string>($"{Key()}", Options, null, Ct);
        await cache.SetMetadataAsync<string>($"{Key()}", Metadata, Ct);
        await typed.RefreshAsync($"{Key()}", Ct);
        await typed.RefreshAsync($"{Key()}", Ttl, Ct);
        await typed.RefreshAsync($"{Key()}", Deadline, Ct);
        await typed.RefreshAsync($"{Key()}", Options, Ct);
        await typed.SetMetadataAsync($"{Key()}", Metadata, Ct);

        await cache.Received(1).RefreshAsync<string>(Expected, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, Ttl, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, Deadline, null, Ct);
        await cache.Received(1).RefreshAsync<string>(Expected, Options, null, Ct);
        await cache.Received(1).SetMetadataAsync<string>(Expected, Metadata, Ct);
        await typed.Received(1).RefreshAsync(Expected, Ct);
        await typed.Received(1).RefreshAsync(Expected, Ttl, Ct);
        await typed.Received(1).RefreshAsync(Expected, Deadline, Ct);
        await typed.Received(1).RefreshAsync(Expected, Options, Ct);
        await typed.Received(1).SetMetadataAsync(Expected, Metadata, Ct);
    }

    [Fact]
    public async Task A_proxy_without_field_removal_rejects_the_span_field_removal_as_it_rejects_the_CacheKey_one()
    {
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();

        var bySpan = async () => await cache.RemoveFieldsAsync<string>($"{Key()}", Fields, Ct);
        var typedBySpan = async () => await typed.RemoveFieldsAsync($"{Key()}", Fields, Ct);

        await bySpan.Should().ThrowAsync<NotSupportedException>();
        await typedBySpan.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task A_cache_with_field_removal_gets_the_span_field_removal_through_its_CacheKey_member()
    {
        var cache = Substitute.For<IHashCache, ISpanKeyHashCache>();
        ((ISpanKeyHashCache)cache).RemoveFieldsAsync<string>(Expected, Fields, Ct).Returns(true);

        (await cache.RemoveFieldsAsync<string>($"{Key()}", Fields, Ct)).Should().BeTrue();

        await ((ISpanKeyHashCache)cache).Received(1).RemoveFieldsAsync<string>(Expected, Fields, Ct);
    }

    [Fact]
    public async Task A_moq_cache_gets_each_span_write_through_its_CacheKey_member()
    {
        var cache = new Mock<ICache>();
        var typed = new Mock<ICache<string>>();
        var hash = new Mock<IHashCache>();
        var typedHash = new Mock<IHashCache<string>>();

        await cache.Object.TryAddAsync<string>($"{Key()}", "v", null, Ct);
        await cache.Object.TryAddAsync<string>($"{Key()}", "v", Ttl, null, Ct);
        await cache.Object.TryAddAsync<string>($"{Key()}", "v", Deadline, null, Ct);
        await cache.Object.RefreshAsync<string>($"{Key()}", null, Ct);
        await cache.Object.RefreshAsync<string>($"{Key()}", Ttl, null, Ct);
        await cache.Object.RefreshAsync<string>($"{Key()}", Deadline, null, Ct);
        await typed.Object.TryAddAsync($"{Key()}", "v", Ct);
        await typed.Object.TryAddAsync($"{Key()}", "v", Ttl, Ct);
        await typed.Object.TryAddAsync($"{Key()}", "v", Deadline, Ct);
        await typed.Object.RefreshAsync($"{Key()}", Ct);
        await typed.Object.RefreshAsync($"{Key()}", Ttl, Ct);
        await typed.Object.RefreshAsync($"{Key()}", Deadline, Ct);
        await hash.Object.RefreshAsync<string>($"{Key()}", null, Ct);
        await hash.Object.RefreshAsync<string>($"{Key()}", Ttl, null, Ct);
        await hash.Object.RefreshAsync<string>($"{Key()}", Deadline, null, Ct);
        await hash.Object.RefreshAsync<string>($"{Key()}", Options, null, Ct);
        await hash.Object.SetMetadataAsync<string>($"{Key()}", Metadata, Ct);
        await typedHash.Object.RefreshAsync($"{Key()}", Ct);
        await typedHash.Object.RefreshAsync($"{Key()}", Ttl, Ct);
        await typedHash.Object.RefreshAsync($"{Key()}", Deadline, Ct);
        await typedHash.Object.RefreshAsync($"{Key()}", Options, Ct);
        await typedHash.Object.SetMetadataAsync($"{Key()}", Metadata, Ct);

        cache.Verify(c => c.TryAddAsync<string>(Expected, "v", null, Ct), Times.Once);
        cache.Verify(c => c.TryAddAsync<string>(Expected, "v", Ttl, null, Ct), Times.Once);
        cache.Verify(c => c.TryAddAsync<string>(Expected, "v", Deadline, null, Ct), Times.Once);
        cache.Verify(c => c.RefreshAsync<string>(Expected, null, Ct), Times.Once);
        cache.Verify(c => c.RefreshAsync<string>(Expected, Ttl, null, Ct), Times.Once);
        cache.Verify(c => c.RefreshAsync<string>(Expected, Deadline, null, Ct), Times.Once);
        typed.Verify(c => c.TryAddAsync(Expected, "v", Ct), Times.Once);
        typed.Verify(c => c.TryAddAsync(Expected, "v", Ttl, Ct), Times.Once);
        typed.Verify(c => c.TryAddAsync(Expected, "v", Deadline, Ct), Times.Once);
        typed.Verify(c => c.RefreshAsync(Expected, Ct), Times.Once);
        typed.Verify(c => c.RefreshAsync(Expected, Ttl, Ct), Times.Once);
        typed.Verify(c => c.RefreshAsync(Expected, Deadline, Ct), Times.Once);
        hash.Verify(c => c.RefreshAsync<string>(Expected, null, Ct), Times.Once);
        hash.Verify(c => c.RefreshAsync<string>(Expected, Ttl, null, Ct), Times.Once);
        hash.Verify(c => c.RefreshAsync<string>(Expected, Deadline, null, Ct), Times.Once);
        hash.Verify(c => c.RefreshAsync<string>(Expected, Options, null, Ct), Times.Once);
        hash.Verify(c => c.SetMetadataAsync<string>(Expected, Metadata, Ct), Times.Once);
        typedHash.Verify(c => c.RefreshAsync(Expected, Ct), Times.Once);
        typedHash.Verify(c => c.RefreshAsync(Expected, Ttl, Ct), Times.Once);
        typedHash.Verify(c => c.RefreshAsync(Expected, Deadline, Ct), Times.Once);
        typedHash.Verify(c => c.RefreshAsync(Expected, Options, Ct), Times.Once);
        typedHash.Verify(c => c.SetMetadataAsync(Expected, Metadata, Ct), Times.Once);
    }

    [Theory]
    [InlineData(typeof(ICache))]
    [InlineData(typeof(ICache<>))]
    [InlineData(typeof(IHashCache))]
    [InlineData(typeof(IHashCache<>))]
    public void Every_single_key_member_of_a_cache_interface_has_an_interpolated_form(Type type)
    {
        var methods = type.GetMethods();
        var interpolatedForms = methods.Where(m => m.GetParameters().Any(p => p.ParameterType.GetElementType() == typeof(CacheKeyHandler))).Select(m => Shape(m, typeof(CacheKeyHandler))).ToHashSet();

        var missing = methods
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(CacheKey)))
            .Select(m => Shape(m, typeof(CacheKey)))
            .Where(shape => !interpolatedForms.Contains(shape))
            .ToArray();

        missing.Should().BeEmpty();
    }

    /// <summary>The member's name, generic arity and parameter types, with the key parameter written as <c>key</c>.</summary>
    private static string Shape(MethodInfo method, Type keyType) =>
        $"{method.Name}`{method.GetGenericArguments().Length}({string.Join(", ", method.GetParameters().Select(p => (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) == keyType ? "key" : p.ParameterType.ToString()))})";

    private static string Key() => " User:42 ";
}
