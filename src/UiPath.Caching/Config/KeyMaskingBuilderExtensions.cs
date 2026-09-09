using UiPath.Caching.Logging;

namespace UiPath.Caching.Config;

/// <summary>Turns key masking on; without one of these the container resolves <see cref="NullKeyMaskingPolicy"/>.</summary>
public static class KeyMaskingBuilderExtensions
{
    /// <summary>Masks the keys starting with one of <paramref name="maskedKeyPrefixes"/>, or every key when none is given.</summary>
    public static ICachingBuilder AddKeyMasking(this ICachingBuilder builder, params string[] maskedKeyPrefixes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddKeyMasking(new PrefixKeyMaskingPolicy(maskedKeyPrefixes));
    }

    public static ICachingBuilder AddKeyMasking(this ICachingBuilder builder, IKeyMaskingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);
        builder.Services.Replace(ServiceDescriptor.Singleton(policy));
        return builder;
    }

    /// <summary>Resolves the policy from the container, so it can take dependencies of its own.</summary>
    public static ICachingBuilder AddKeyMasking<TPolicy>(this ICachingBuilder builder)
        where TPolicy : class, IKeyMaskingPolicy
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IKeyMaskingPolicy, TPolicy>());
        return builder;
    }
}
