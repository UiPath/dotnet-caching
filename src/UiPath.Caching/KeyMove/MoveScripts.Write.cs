using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    /// <summary>What the fallback read and decided: the code to return, and how to queue its writes; null when it has none to run.</summary>
    internal readonly record struct FallbackPlan(long Code, Action<ITransaction, List<Task>>? Apply);

    /// <summary>One move write as the script and as the same writes under the same conditions for a server without scripts; it runs alone, or inside the transaction of a <see cref="ServerBarrier"/>.</summary>
    internal sealed class Write(MoveScripts owner, string script, RedisKey[] keys, RedisValue[] args, Func<Task<FallbackPlan>> fallback)
    {
        private Task<RedisResult>? _scripted;
        private FallbackPlan? _plan;
        private bool _asScript;

        /// <summary>The code of the last attempt that queued this write and ran: the script's reply, or the fallback's. Null while none did, or when the transaction was discarded.</summary>
        public long? Code
        {
            get
            {
                if (!_asScript)
                {
                    return _plan?.Code;
                }

                return _scripted is { IsCompletedSuccessfully: true } done ? (long)done.Result : null;
            }
        }

        /// <summary>The script, or once scripts are off the watched fallback transaction.</summary>
        public async Task<long> RunAsync()
        {
            var reply = await owner.EvaluateAsync(script, keys, args).ConfigureAwait(false);
            if (reply is not null)
            {
                return (long)reply;
            }

            return await WatchedAsync(async () =>
            {
                var plan = await fallback().ConfigureAwait(false);
                if (plan.Apply is not { } apply)
                {
                    return plan.Code;
                }

                var transaction = owner._db.CreateTransaction();
                var queued = new List<Task>();
                apply(transaction, queued);
                return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? plan.Code : null;
            }).ConfigureAwait(false);
        }

        /// <summary>Before every attempt of a barrier: the fallback reads the key, so an attempt that is repeated sees what the last one left. Scripts need nothing.</summary>
        public async Task PrepareAsync()
        {
            _scripted = null;
            _plan = null;
            _asScript = owner.UsesScripts;
            if (!_asScript)
            {
                owner.EnsureFallbackAllowed();
                _plan = await fallback().ConfigureAwait(false);
            }
        }

        /// <summary>Queues the write on the transaction of a barrier, as prepared.</summary>
        public void Queue(ITransaction transaction)
        {
            if (_asScript)
            {
                _scripted = transaction.ScriptEvaluateAsync(script, keys, args, Master);
            }
            else if (_plan is { Apply: { } apply })
            {
                apply(transaction, []);
            }
            else
            {
                // Nothing to write: a read of the key still routes the transaction, and its proof, to the key's master.
                _ = transaction.KeyExistsAsync(keys[0], Master);
            }
        }

        /// <summary>Whether the server refuses scripts, found by sending a trivial one alone; a refusal also turns scripts off, as in <see cref="RunAsync"/>, since a discarded transaction only says EXECABORT.</summary>
        public async Task<bool> ScriptsRefusedAsync()
        {
            if (!owner.UsesScripts)
            {
                return false;
            }

            try
            {
                await owner._db.ScriptEvaluateAsync("return 1", keys, null, Master).ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                if (Interlocked.Exchange(ref owner._useScripts, 0) == 1)
                {
                    owner._scriptRefused?.Invoke(ex);
                }

                return true;
            }
        }
    }
}
