using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>
/// What the router's entry in the node hash and the pin state need, built from the router's configuration section, bound as the named stack binds it, and the process id, never from the library's
/// container, so a library build that hangs or throws cannot hold or disable them. The router's stack binds the same configuration section, so the identity
/// and the node-list key agree with it.
/// </summary>
internal sealed record AdvertisementSetup(
    RouterAdvertisementStore Store,
    IPinStore PinStore,
    string NodeId,
    string Build,
    string? Scope,
    TimeSpan RefreshInterval,
    TimeSpan LivenessWindow,
    TimeSpan MaxViewAge)
{
    /// <summary>Null, logged, when the options cannot be read: the router then runs without an entry, and its pin has no store.</summary>
    public static AdvertisementSetup? TryCreate(IServiceProvider root, RouterBuilder builder, string processId, IConfigurationSection section, ILogger logger)
    {
        try
        {
            var cacheOptions = new CacheOptions();
            section.Bind(cacheOptions);
            var redisOptions = root.GetService<IOptions<RedisCacheOptions>>()?.Value;
            // The one timing source: the router's own section, which the stack does not bind for itself (its Redis options are set in code).
            var negotiation = new FormatNegotiationOptions();
            section.GetSection("Redis:FormatNegotiation").Bind(negotiation);
            if (negotiation.AdvertiseInterval <= TimeSpan.Zero || negotiation.LivenessWindow <= TimeSpan.Zero)
            {
                logger.LogWarning("The router runs without a node advertisement: Redis:FormatNegotiation AdvertiseInterval and LivenessWindow must be positive.");
                return null;
            }

            Func<CancellationToken, ValueTask<IDatabase>> database = builder.SourceDatabase is { } source
                ? _ => ValueTask.FromResult(source())
                : async token =>
                {
                    var connector = root.GetRequiredService<IRedisConnector>();
                    await connector.ConnectAsync(token).ConfigureAwait(false);
                    return connector.Database;
                };
            var store = new RouterAdvertisementStore(database, RouterServiceCollectionExtensions.NodeListKey(new CacheOptions { Separator = cacheOptions.Separator }, new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance }));
            var scope = ScopeTokens.For(cacheOptions.AppShortName);

            // A source database adds no prefix of its own, so the key carries it; the library's connection adds it itself.
            var prefix = builder.SourceDatabase is null ? string.Empty : redisOptions?.KeyPrefix ?? string.Empty;
            return new AdvertisementSetup(
                store,
                new RedisPinStore(store, prefix, scope),
                RouterServiceCollectionExtensions.StackIdentity(cacheOptions.SourceUri ?? CacheOptions.MachineUri, processId).ToString().ToLowerInvariant(),
                typeof(Router).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
                scope,
                negotiation.AdvertiseInterval,
                negotiation.LivenessWindow,
                negotiation.MaxViewAge);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The router could not set up its node advertisement; it runs without one.");
            return null;
        }
    }
}
