using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace UiPath.Caching.Redis;

/// <summary>The prefix a connector's database prepends, resolved once per connector so <see cref="RedisCacheOptions.KeyPrefix"/> cannot drift from it.</summary>
internal sealed partial class ConnectorKeyPrefix
{
    internal const string ProbeMarker = "uipath-caching-key-prefix-probe";

    private const int MaxProbeAttempts = 3;
    private const string KeyPrefixedTypeName = "StackExchange.Redis.KeyspaceIsolation.KeyPrefixed`1";
    private const string BareDatabaseTypeName = "StackExchange.Redis.RedisDatabase";

    private static readonly ConditionalWeakTable<IRedisConnector, ConnectorKeyPrefix> Instances = [];
    private static readonly byte[] ProbeMarkerBytes = Encoding.ASCII.GetBytes(ProbeMarker);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // SCAN MATCH reads these as glob syntax, and the maintainer composes its pattern from the prefix.
    private static readonly char[] GlobCharacters = ['*', '?', '[', ']', '\\'];

    private readonly object _gate = new();
    private string? _prefix;
    private string? _skipped;
    private Task<bool>? _probe;
    private int _attempts;
    private int _skipLogged;
    private int _configuredSkipLogged;
    private int _mismatchLogged;

    private ConnectorKeyPrefix()
    {
    }

    public static ConnectorKeyPrefix For(IRedisConnector connector) => Instances.GetValue(connector, static _ => new ConnectorKeyPrefix());

    /// <summary>Never waits on the server: while a probe is running, <paramref name="configured"/> stands in, and the probe's answer is reported once it lands.</summary>
    public string Get(IDatabase database, string configured, ILogger logger)
    {
        var configuredUsable = IsUsable(configured);
        var fallback = configuredUsable ? configured : string.Empty;
        string prefix;
        if (TryGetKnown(database, fallback, out var known))
        {
            prefix = known;
        }
        else
        {
            var probe = Probe(database);
            if (!probe.IsCompleted)
            {
                ReportWhenAnsweredAsync(probe, logger, configured, configuredUsable, fallback).Forget();
            }
            prefix = probe.IsCompletedSuccessfully && probe.Result ? Volatile.Read(ref _prefix) ?? fallback : fallback;
        }

        Report(logger, configured, configuredUsable, prefix);
        return prefix;
    }

    /// <summary>Waits for a running probe.</summary>
    public async ValueTask<string> GetAsync(IDatabase database, string configured, ILogger logger, CancellationToken cancellationToken)
    {
        var configuredUsable = IsUsable(configured);
        var fallback = configuredUsable ? configured : string.Empty;
        string prefix;
        if (TryGetKnown(database, fallback, out var known))
        {
            prefix = known;
        }
        else if (await Probe(database).WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            prefix = Volatile.Read(ref _prefix) ?? fallback;
        }
        else
        {
            prefix = fallback;
        }

        Report(logger, configured, configuredUsable, prefix);
        return prefix;
    }

    /// <summary>Recognizes StackExchange.Redis's own databases; a decorator around one reads as unknown.</summary>
    [SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The wrapper's Prefix is internal; a failed read falls back to the probe")]
    internal static bool TryRead(IDatabase database, out byte[] prefix)
    {
        prefix = [];
        try
        {
            for (var type = database.GetType(); type is not null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == KeyPrefixedTypeName)
                {
                    if (type.GetProperty("Prefix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(database) is not byte[] bytes)
                    {
                        return false;
                    }

                    prefix = bytes;
                    return true;
                }
            }

            return database.GetType().FullName == BareDatabaseTypeName;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A prefixing database prepends its prefix to every key argument, so ECHO returns it ahead of the marker.</summary>
    private static async Task<byte[]?> EchoAsync(IDatabase database)
    {
        try
        {
            var echoed = (byte[]?)await database.ExecuteAsync("ECHO", [(RedisKey)ProbeMarker], CommandFlags.None).ConfigureAwait(false);
            return echoed is not null && echoed.AsSpan().EndsWith(ProbeMarkerBytes) ? echoed[..^ProbeMarkerBytes.Length] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The library carries the prefix as text and composes SCAN patterns from it, so only UTF-8 without glob characters is usable.</summary>
    private static bool TryDecode(byte[] prefix, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(prefix);
        }
        catch (DecoderFallbackException)
        {
            text = "0x" + Convert.ToHexString(prefix);
            return false;
        }

        return IsUsable(text);
    }

    private static bool IsUsable(string prefix) => prefix.IndexOfAny(GlobCharacters) < 0;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The connector's database prepends '{Prefix}', which is not UTF-8 or holds a SCAN glob character (* ? [ ] or backslash); it is skipped and KeyPrefix '{Configured}' is used instead")]
    private static partial void LogKeyPrefixSkipped(ILogger logger, string prefix, string configured);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RedisCacheOptions.KeyPrefix '{Configured}' holds a SCAN glob character (* ? [ ] or backslash); it is skipped and no prefix is assumed in its place")]
    private static partial void LogConfiguredKeyPrefixSkipped(ILogger logger, string configured);

    [LoggerMessage(Level = LogLevel.Error, Message = "RedisCacheOptions.KeyPrefix is '{Configured}' but the connector's database prepends '{Resolved}'; '{Resolved}' is used")]
    private static partial void LogKeyPrefixMismatch(ILogger logger, string configured, string resolved);

    /// <summary>Each skipped prefix, and a configured one that disagrees with the connector's, is logged once per connector.</summary>
    private void Report(ILogger logger, string configured, bool configuredUsable, string prefix)
    {
        if (!configuredUsable && Interlocked.Exchange(ref _configuredSkipLogged, 1) == 0)
        {
            LogConfiguredKeyPrefixSkipped(logger, configured);
        }

        if (Volatile.Read(ref _skipped) is { } skipped)
        {
            if (Interlocked.Exchange(ref _skipLogged, 1) == 0)
            {
                LogKeyPrefixSkipped(logger, skipped, configuredUsable ? configured : string.Empty);
            }
        }
        else if (configuredUsable && configured.Length > 0 && Volatile.Read(ref _prefix) is not null
            && !string.Equals(configured, prefix, StringComparison.Ordinal)
            && Interlocked.Exchange(ref _mismatchLogged, 1) == 0)
        {
            LogKeyPrefixMismatch(logger, configured, prefix);
        }
    }

    /// <summary>A caller that did not wait may make no further call, so its diagnostics come from the answer.</summary>
    private async Task ReportWhenAnsweredAsync(Task<bool> probe, ILogger logger, string configured, bool configuredUsable, string fallback)
    {
        var prefix = await probe.ConfigureAwait(false) ? Volatile.Read(ref _prefix) ?? fallback : fallback;
        Report(logger, configured, configuredUsable, prefix);
    }

    private bool TryGetKnown(IDatabase database, string configured, out string prefix)
    {
        if (Volatile.Read(ref _prefix) is { } kept)
        {
            prefix = kept;
            return true;
        }

        if (Volatile.Read(ref _skipped) is not null)
        {
            prefix = configured;
            return true;
        }

        if (TryRead(database, out var read))
        {
            prefix = Accept(read) ? Volatile.Read(ref _prefix)! : configured;
            return true;
        }

        prefix = string.Empty;
        return false;
    }

    /// <summary>Keeps a usable prefix, or records the connector's as skipped so it is neither used nor probed again.</summary>
    private bool Accept(byte[] prefix)
    {
        if (TryDecode(prefix, out var text))
        {
            Interlocked.CompareExchange(ref _prefix, text, null);
            return true;
        }

        Interlocked.CompareExchange(ref _skipped, text, null);
        return false;
    }

    /// <summary>At most one probe runs at a time, and a failed one is retried by a later call until the attempts run out.</summary>
    private Task<bool> Probe(IDatabase database)
    {
        lock (_gate)
        {
            if (_probe is { IsCompleted: false } running)
            {
                return running;
            }

            if (_attempts >= MaxProbeAttempts)
            {
                return Task.FromResult(false);
            }

            _attempts++;
            _probe = ProbeOnceAsync(database);
            return _probe;
        }
    }

    /// <summary>True once the connector answered, whether its prefix was kept or skipped.</summary>
    private async Task<bool> ProbeOnceAsync(IDatabase database)
    {
        var prefix = await EchoAsync(database).ConfigureAwait(false);
        if (prefix is null)
        {
            return false;
        }

        Accept(prefix);
        return true;
    }
}
