using System.Security.Cryptography;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The scripts of a move, each refused unless the control record holds the caller's identity, with watched-transaction fallbacks for servers that refuse scripts.</summary>
internal sealed partial class MoveScripts
{
    public const long NoExpiry = -1;
    public const long IdentityMismatch = -1;
    public const long GateRefused = -2;
    public const long PastDeadline = -3;
    public const long IdentityConflict = -5;

    /// <summary>How often a transaction is sent again when every condition still holds after it aborted: another field of a watched key changed.</summary>
    internal const int MaxWatchAborts = 16;

    internal static readonly Version FallbackMinimum = new(6, 0, 9);

    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly IDatabase _db;
    private readonly Version? _serverVersion;
    private readonly Action<Exception>? _scriptRefused;
    private int _useScripts;

    /// <param name="serverVersion">The oldest server version of the deployment; the fallback runs only on 6.0.9 or later, and never when this is unknown.</param>
    /// <param name="scriptRefused">Told once, when the server first refuses a script.</param>
    public MoveScripts(IDatabase db, bool useScripts, Version? serverVersion, Action<Exception>? scriptRefused = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _serverVersion = serverVersion;
        _scriptRefused = scriptRefused;
        _useScripts = useScripts ? 1 : 0;
        if (!useScripts)
        {
            EnsureFallbackAllowed();
        }
    }

    public bool UsesScripts => Volatile.Read(ref _useScripts) == 1;

    /// <summary>A fresh 128-bit nonce in hex, for fences, gates and staging keys.</summary>
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static RedisValue Ttl(long px) => px == NoExpiry ? "-1" : px;

    private static void CheckLife(long px, string paramName)
    {
        if (px != NoExpiry && px < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, px, "A remaining life is -1 for none or at least 1 ms; a shorter one is an expired value and is never sent.");
        }
    }

    private static bool IsWrongType(Exception ex) =>
        ex is RedisServerException && ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase);

    private static DateTime At(long unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;

    private static async Task<long> WatchedAsync(Func<Task<long?>> attempt)
    {
        for (var i = 0; i < MaxWatchAborts; i++)
        {
            if (await attempt().ConfigureAwait(false) is { } result)
            {
                return result;
            }
        }

        throw new RedisException($"A move transaction aborted {MaxWatchAborts} times while its conditions held; the outcome is unknown.");
    }

    // EXEC commits even when one of its commands fails, for example on a key of another type; the script would raise that error, so this does too.
    private static async Task<bool> ExecuteAllAsync(ITransaction transaction, List<Task> queued)
    {
        if (!await transaction.ExecuteAsync(Master).ConfigureAwait(false))
        {
            return false;
        }

        await Task.WhenAll(queued).ConfigureAwait(false);
        return true;
    }

    private void EnsureFallbackAllowed()
    {
        if (_serverVersion is null || _serverVersion < FallbackMinimum)
        {
            throw new NotSupportedException(
                $"The server refuses scripts and runs Redis {_serverVersion?.ToString() ?? "of an unknown version"}; a move without scripts needs {FallbackMinimum} or later, where a watched key that expires aborts the transaction.");
        }
    }

    /// <summary>The script's reply, or null once scripts are off and the caller takes the fallback.</summary>
    private async Task<RedisResult?> EvaluateAsync(string script, RedisKey[] keys, RedisValue[] args)
    {
        if (UsesScripts)
        {
            try
            {
                return await _db.ScriptEvaluateAsync(script, keys, args, Master).ConfigureAwait(false);
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                if (Interlocked.Exchange(ref _useScripts, 0) == 1)
                {
                    _scriptRefused?.Invoke(ex);
                }
            }
        }

        EnsureFallbackAllowed();
        return null;
    }

    /// <summary>The clock of the server that holds <paramref name="key"/>, in Unix milliseconds.</summary>
    private async Task<long> ServerTimeAsync(RedisKey key)
    {
        var endpoint = await _db.IdentifyEndpointAsync(key, Master).ConfigureAwait(false)
            ?? throw new RedisException($"No server holds the slot of '{key}'.");
        var time = await _db.Multiplexer.GetServer(endpoint).TimeAsync(Master).ConfigureAwait(false);
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    private async Task<bool> HoldsIdentityAsync(RedisKey control, string id) =>
        await _db.HashGetAsync(control, "id", Master).ConfigureAwait(false) == id;
}
