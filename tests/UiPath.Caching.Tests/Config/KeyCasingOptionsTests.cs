using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Config;

[CollectionDefinition("CacheKeyDefaultCasing", DisableParallelization = true)]
public class CacheKeyDefaultCasingCollection;

[Collection("CacheKeyDefaultCasing")]
public class KeyCasingOptionsTests
{
    /// <summary>
    /// The section-only overload must bind <see cref="CacheOptions"/>; passing the binder positionally made it
    /// land on the builder parameter, leaving the section unread and the casing silently at its default.
    /// </summary>
    [Fact]
    public void Section_only_overload_binds_key_casing()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Caching:KeyCasing"] = nameof(CacheKeyCasing.Sensitive) })
                .Build();
            var services = new ServiceCollection();

            services.AddCaching(configuration.GetSection("Caching"));
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<CacheOptions>>().Value.KeyCasing
                .Should().Be(CacheKeyCasing.Sensitive);
            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Sensitive);
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }

    /// <summary>
    /// Configuration binds enums numerically without validating them, and treating an unknown value as
    /// sensitive would silently stop lowercasing and relocate every key.
    /// </summary>
    [Fact]
    public void Unsupported_key_casing_value_fails_fast()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCaching(_ => { }, o => o.KeyCasing = (CacheKeyCasing)2);

        act.Should().Throw<InvalidOperationException>().WithMessage("*KeyCasing*unsupported value 2*");
    }

    /// <summary>
    /// Options registered after AddCaching bypass the eager seed and reach only the PostConfigure callback, so
    /// that callback validates too — otherwise resolving options succeeds and the next key built throws.
    /// </summary>
    [Fact]
    public void Unsupported_key_casing_configured_after_AddCaching_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(_ => { });
        services.Configure<CacheOptions>(o => o.KeyCasing = (CacheKeyCasing)3);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*KeyCasing*unsupported value 3*");
    }

    /// <summary>
    /// Post-configure callbacks run in registration order, so seeding from one would leave the global default
    /// at whatever was seeded first while the resolved options reported the consumer's later value.
    /// </summary>
    [Fact]
    public void Casing_post_configured_after_AddCaching_still_reaches_the_global_default()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddCaching(_ => { }, o => o.KeyCasing = CacheKeyCasing.Insensitive);
            services.PostConfigure<CacheOptions>(o => o.KeyCasing = CacheKeyCasing.Sensitive);
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<CacheOptions>>().Value.KeyCasing
                .Should().Be(CacheKeyCasing.Sensitive);
            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Sensitive);
            new CacheKey("AbC").Name.Should().Be("AbC");
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }

    /// <summary>The global default is state every key reads, so a bad value is refused where it is assigned.</summary>
    [Fact]
    public void Unsupported_default_casing_is_rejected_by_the_setter()
    {
        var act = () => CacheKey.DefaultCasing = (CacheKeyCasing)2;

        act.Should().Throw<ArgumentOutOfRangeException>();
        CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Insensitive, "the assignment was refused");
    }

    /// <summary>Same guarantee at the key itself, for a casing value that never passed through the options.</summary>
    [Fact]
    public void Unsupported_key_casing_is_rejected_by_the_key()
    {
        var act = () => new CacheKey("AbC", (CacheKeyCasing)7);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void KeyCasing_defaults_to_insensitive()
    {
        new CacheOptions().KeyCasing.Should().Be(CacheKeyCasing.Insensitive);
    }

    [Fact]
    public void Resolving_options_seeds_the_ambient_default()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddCaching(_ => { }, o => o.KeyCasing = CacheKeyCasing.Sensitive);
            using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Sensitive);
            new CacheKey("AbC").Name.Should().Be("AbC");
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }

    [Fact]
    public void Insensitive_configuration_keeps_lowercasing()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddCaching(_ => { });
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Insensitive);
            new CacheKey("AbC").Name.Should().Be("abc");
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }
}
