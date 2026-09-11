namespace UiPath.Caching.Redis;

public static class ReservedRedisKeyspaceExtensions
{
    private const string DistributedCacheOwner = "AddDistributedCache";

    /// <summary>
    /// Declares a keyspace this package occupies; call it from the package's own builder extension. The
    /// keyspace is one segment of letters and digits. A different owner already on it is rejected, a repeat
    /// by the same owner is ignored so a package can reserve from several registration methods, and keyspace
    /// comparison is case-insensitive because the key strategy lowercases. <paramref name="owner"/> is
    /// matched exactly, so qualify it with the package name.
    /// </summary>
    public static IServiceCollection ReserveRedisKeyspace(this IServiceCollection services, string keyspace, string owner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(owner);
        return services.Reserve(new ReservedRedisKeyspace(keyspace, owner));
    }

    /// <summary>
    /// Reserves the keyspace <c>AddDistributedCache</c> takes with its differentiator. The reservation is
    /// marked by type rather than by owner, which is display text a caller could imitate.
    /// </summary>
    internal static IServiceCollection ReserveDistributedCacheRedisKeyspace(this IServiceCollection services, string differentiator) =>
        services.Reserve(new ReservedRedisKeyspace(differentiator, DistributedCacheOwner, isDistributedCache: true));

    /// <summary>
    /// The declarations made so far. Only instance registrations are visible before the container exists,
    /// so a reservation added any other way would be enforced at resolution but not at registration.
    /// </summary>
    internal static IEnumerable<IReservedRedisKeyspace> ReservedRedisKeyspaces(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IReservedRedisKeyspace)))
        {
            yield return descriptor.ImplementationInstance as ReservedRedisKeyspace ?? throw Unsupported();
        }
    }

    /// <summary>Reservations that reached the container any other way, which the registration-time check never saw.</summary>
    internal static IEnumerable<IReservedRedisKeyspace> Validated(this IEnumerable<IReservedRedisKeyspace> reserved) =>
        reserved.Select(r => r as ReservedRedisKeyspace ?? throw Unsupported());

    /// <summary>
    /// The separator is only known once options bind, so this is where one keyspace containing another is
    /// caught: with separator <c>'x'</c>, <c>"a"</c> and <c>"axb"</c> render the same key from <c>"bxk"</c>
    /// and <c>"k"</c>.
    /// </summary>
    internal static IReadOnlyList<IReservedRedisKeyspace> ValidatedFor(
        this IEnumerable<IReservedRedisKeyspace> reserved, char separator)
    {
        var all = reserved.Validated().ToList();
        foreach (var outer in all)
        {
            var nested = all.Find(other => !ReferenceEquals(other, outer)
                && other.Keyspace.StartsWith(outer.Keyspace + separator, StringComparison.OrdinalIgnoreCase));
            if (nested is not null)
            {
                throw new InvalidOperationException(
                    $"The Redis keyspace '{nested.Keyspace}' reserved by {nested.Owner} sits inside '{outer.Keyspace}' reserved by {outer.Owner} under the configured separator '{separator}', so the two would render the same key from different cache keys. Choose keyspaces that do not nest, or a separator that does not appear in them.");
            }
        }

        return all;
    }

    internal static bool Covers(this IReservedRedisKeyspace reserved, string keyspace) =>
        string.Equals(reserved.Keyspace, keyspace, StringComparison.OrdinalIgnoreCase);

    internal static bool IsDistributedCache(this IReservedRedisKeyspace reserved) =>
        reserved is ReservedRedisKeyspace { IsDistributedCache: true };

    /// <summary>Whether <c>AddDistributedCache</c> already took a keyspace, enabled or not.</summary>
    internal static bool HasDistributedCacheRedisKeyspace(this IServiceCollection services) =>
        services.ReservedRedisKeyspaces().ToList().Exists(reserved => reserved.IsDistributedCache());

    private static IServiceCollection Reserve(this IServiceCollection services, ReservedRedisKeyspace reservation)
    {
        // Materialized, so every descriptor is validated even when the match is found early.
        var reserved = services.ReservedRedisKeyspaces().ToList();
        if (reserved.FirstOrDefault(r => r.Covers(reservation.Keyspace)) is { } taken)
        {
            if (string.Equals(taken.Owner, reservation.Owner, StringComparison.Ordinal)
                && taken.IsDistributedCache() == reservation.IsDistributedCache)
            {
                return services;
            }

            throw new InvalidOperationException(KeyspaceCollisionMessage(taken, reservation));
        }

        services.AddSingleton<IReservedRedisKeyspace>(reservation);
        return services;
    }

    private static InvalidOperationException Unsupported() =>
        new("An IReservedRedisKeyspace is registered by something other than IServiceCollection.ReserveRedisKeyspace(keyspace, owner) — from a factory, an implementation type, or a custom implementation. Reservations made that way are invisible to the registration-time keyspace check, so they cannot be checked against each other. Register it with ReserveRedisKeyspace instead.");

    /// <summary>Names the differentiator when the distributed cache is one of the two sides, since that is the value the reader can change.</summary>
    private static string KeyspaceCollisionMessage(IReservedRedisKeyspace taken, ReservedRedisKeyspace wanted)
    {
        if (wanted.IsDistributedCache)
        {
            return $"UiPathDistributedCacheOptions.RedisKeyDifferentiator '{wanted.Keyspace}' is the Redis keyspace reserved by {taken.Owner}, which would put the distributed cache in the same Redis keyspace. Choose another value.";
        }

        if (taken.IsDistributedCache())
        {
            return $"UiPathDistributedCacheOptions.RedisKeyDifferentiator '{taken.Keyspace}' is the Redis keyspace reserved by {wanted.Owner}, which would put the distributed cache in the same Redis keyspace. Choose another value.";
        }

        return $"{wanted.Owner} reserves the Redis keyspace '{wanted.Keyspace}', which {taken.Owner} already occupies. Two packages cannot share one keyspace: their keys would collide on Redis.";
    }
}
