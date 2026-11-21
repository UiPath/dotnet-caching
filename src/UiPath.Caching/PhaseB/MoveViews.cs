using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The connector views of an instance move (section 3.2): the Src prefixed view for data, fences, control records, the claim, node list, locks and events;
/// the Src raw view for scans only; one unprefixed Dst view. The key map strips and restores the Src connection prefix.
/// </summary>
/// <remarks>The views keep the connectors and resolve their databases on every access, because a reconnect replaces and disposes the multiplexer.</remarks>
internal sealed class MoveViews : IDisposable
{
    private readonly Func<IDatabase> _source;
    private readonly Func<IDatabase> _destination;

    private MoveViews(Func<IDatabase> source, Func<IDatabase> destination, string sourcePrefix, ConnectionChanges changes)
    {
        _source = source;
        _destination = destination;
        SourcePrefix = sourcePrefix;
        Changes = changes;
    }

    /// <summary>The Src view, resolved from the connector on every access, so a reconnect is followed.</summary>
    public IDatabase SourcePrefixed => _source();

    /// <summary>Scans only: patterns start with <see cref="SourcePrefix"/> and results are stripped by <see cref="ToLogical"/>.</summary>
    public IDatabase SourceRaw
    {
        get
        {
            var prefixed = SourcePrefixed;
            return prefixed.Multiplexer.GetDatabase(prefixed.Database);
        }
    }

    /// <summary>The Dst view, resolved from the connector on every access.</summary>
    public IDatabase Destination => _destination();

    /// <summary>The prefix the Src connection puts in front of every key; empty for none.</summary>
    public string SourcePrefix { get; }

    /// <summary>Connection events on both servers, for the promotion's abandon rule.</summary>
    public ConnectionChanges Changes { get; }

    /// <param name="configuredSourcePrefix">The Src connector's configured key prefix, which the connector's own prefix detection confirms.</param>
    public static async Task<MoveViews> CreateAsync(IRedisConnector source, IRedisConnector destination, string configuredSourcePrefix, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var prefix = await ConnectorKeyPrefix.For(source).GetAsync(source.Database, configuredSourcePrefix, logger, cancellationToken).ConfigureAwait(false);
        return new MoveViews(() => source.Database, () => destination.Database, prefix, new ConnectionChanges(source, destination));
    }

    /// <summary>K2: a raw Src key without the connection prefix; null for a key outside it, which is not this move's.</summary>
    public string? ToLogical(RedisKey raw)
    {
        var text = (string?)raw;
        return text is not null && text.StartsWith(SourcePrefix, StringComparison.Ordinal) ? text[SourcePrefix.Length..] : null;
    }

    /// <summary>K1: the raw Src key of a logical name.</summary>
    public RedisKey ToRaw(string logical) => SourcePrefix + logical;

    /// <summary>The Src scan pattern for a logical pattern, in the form the server stores keys.</summary>
    public string SourceScanPattern(string logicalPattern) => SourcePrefix + logicalPattern;

    public void Dispose() => Changes.Dispose();

    /// <summary>Views over fixed databases, with the Src connection prefix known.</summary>
    internal static MoveViews Create(IDatabase sourcePrefixed, IDatabase destination, string sourcePrefix) =>
        new(() => sourcePrefixed, () => destination, sourcePrefix, new ConnectionChanges(sourcePrefixed.Multiplexer, destination.Multiplexer));
}
