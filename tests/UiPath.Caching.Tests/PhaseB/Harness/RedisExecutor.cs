using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB.Harness;

// Runs the same conformance steps through MoveScripts against a real server, with the roles mapped to fresh keys.
internal sealed class RedisExecutor(MoveTestEnvironment env, long now)
{
    public static readonly string[] Roles = ["fence", "dst", "stg", "ctl", "src"];

    private readonly Dictionary<string, RedisKey> _keys = Roles.ToDictionary(r => r, env.Make);

    private FencedKeys Fenced => new(_keys["fence"], _keys["dst"], _keys["ctl"]);

    public async Task<string> RunAsync(ScriptCall c)
    {
        try
        {
            return await ExecuteAsync(c).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return "WRONGTYPE";
        }
    }

    public async Task<(string Content, long Ttl)> DumpAsync(string role)
    {
        var key = _keys[role];
        var type = await env.Db.KeyTypeAsync(key).ConfigureAwait(false);
        if (type == RedisType.None)
        {
            return ("none", -1);
        }

        var ttl = (long)(await env.Db.ExecuteAsync("PTTL", key).ConfigureAwait(false));
        if (type == RedisType.String)
        {
            return ("string:" + await env.Db.StringGetAsync(key).ConfigureAwait(false), ttl);
        }

        var fields = (await env.Db.HashGetAllAsync(key).ConfigureAwait(false)).OrderBy(e => (string)e.Name!, StringComparer.Ordinal);
        return ("hash:" + string.Join(';', fields.Select(e => $"{e.Name}={e.Value}")), ttl);
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private long At(ScriptCall c) => c.AtOffset is { } o ? now + o : -1;

    private async Task<string> ExecuteAsync(ScriptCall c)
    {
        var db = env.Db;
        var scripts = env.Scripts;
        var key = _keys[c.Key];
        var ctl = _keys["ctl"];
        var src = _keys["src"];
        var stg = _keys["stg"];
        var fence = c.WithFence ? new FenceBump(_keys["fence"], c.Nonce, c.FenceTtl) : (FenceBump?)null;
        var entries = ModelExecutor.Pairs(c.Fields).Select(p => new HashEntry(p.Key, p.Value)).ToArray();
        switch (c.Kind)
        {
            case ScriptCallKind.PutString:
                await db.StringSetAsync(key, c.Value).ConfigureAwait(false);
                return string.Empty;
            case ScriptCallKind.PutHash:
                await db.HashSetAsync(key, entries).ConfigureAwait(false);
                return string.Empty;
            case ScriptCallKind.Delete:
                await db.KeyDeleteAsync(key).ConfigureAwait(false);
                return string.Empty;
            case ScriptCallKind.ExpireIn:
                await db.KeyExpireAsync(key, TimeSpan.FromMilliseconds(c.Px)).ConfigureAwait(false);
                return string.Empty;
            case ScriptCallKind.Cap:
            {
                var r = await scripts.CaptureAsync(Fenced, c.Id, c.Cid, c.Nonce, c.FenceTtl).ConfigureAwait(false);
                return $"{r.Code}|{r.Token}|{r.DestinationExists}|{r.Gate}";
            }

            case ScriptCallKind.Pub:
                return Text(await scripts.PublishAsync(Fenced, c.Id, c.Value, new Publication(c.Token, c.Gate, c.Cid, c.Nonce, c.FenceTtl, c.Px, c.AtOffset is null ? -1 : At(c), c.Mode == "nx")).ConfigureAwait(false));
            case ScriptCallKind.PubHash:
                return Text(await scripts.PublishHashAsync(Fenced, stg, c.Id, new Publication(c.Token, c.Gate, c.Cid, c.Nonce, c.FenceTtl, c.Px, At(c), c.Mode == "nx")).ConfigureAwait(false));
            case ScriptCallKind.Bump:
            {
                var op = c.Op == "set" ? BumpOperation.Set : c.Op == "del" ? BumpOperation.Delete : BumpOperation.None;
                return Text(await scripts.BumpAsync(Fenced, c.Id, c.Nonce, c.FenceTtl, op, c.Value, c.Px).ConfigureAwait(false));
            }

            case ScriptCallKind.Fill:
                return Text(await scripts.FillAsync(Fenced, c.Id, c.Value, c.Px, c.Nonce, c.FenceTtl).ConfigureAwait(false));
            case ScriptCallKind.FillHash:
                return Text(await scripts.FillHashAsync(Fenced, stg, c.Id, c.Px, c.Nonce, c.FenceTtl).ConfigureAwait(false));
            case ScriptCallKind.SrcDel:
                return Text(await scripts.SourceDeleteAsync(src, ctl, c.Id).ConfigureAwait(false));
            case ScriptCallKind.SrcSet:
                return Text(await scripts.SourceSetAsync(src, ctl, c.Id, c.Value, c.Px, c.Only).ConfigureAwait(false));
            case ScriptCallKind.SrcExp:
                return Text(await scripts.SourceExpireAsync(src, ctl, c.Id, c.Px).ConfigureAwait(false));
            case ScriptCallKind.CmpDel:
                return Text(await scripts.CompareDeleteAsync(src, ctl, c.Id, c.Value).ConfigureAwait(false));
            case ScriptCallKind.CmpDelHash:
                return Text(await scripts.CompareDeleteHashAsync(src, ctl, c.Id, entries).ConfigureAwait(false));
            case ScriptCallKind.CtlSync:
                return Text(await scripts.ControlSyncAsync(ctl, new ControlRecord(c.Ep, c.Id, "S1", "fwd", "n", "S0", "none", "n0", "0", "id0", [(c.Cid, c.Nonce)])).ConfigureAwait(false));
            case ScriptCallKind.CtlCommit:
                return Text(await scripts.ControlCommitAsync(ctl, c.Ep, c.Id).ConfigureAwait(false));
            case ScriptCallKind.Gate:
                return Text(await scripts.GateAsync(ctl, c.Id, c.Cid, c.Value, c.Expect).ConfigureAwait(false));
            case ScriptCallKind.HSetC:
            {
                var mode = c.ReplaceKey ? HashCacheSetOption.KeyReplace : HashCacheSetOption.HashReplace;
                return Text(await scripts.HashWriteAsync(src, ctl, c.Id, new ControlledHashWrite(entries, mode, c.AtOffset is null ? -1 : At(c), c.DropField, c.VersionField, c.VersionSeed), fence).ConfigureAwait(false));
            }

            case ScriptCallKind.HDelC:
                return Text(await scripts.HashDeleteFieldsAsync(src, ctl, c.Id, [.. entries.Select(e => e.Name)], c.VersionField, c.VersionSeed, fence).ConfigureAwait(false));
            default:
                return Text(await scripts.HashExpireAsync(src, ctl, c.Id, c.AtOffset is null ? -1 : At(c), fence).ConfigureAwait(false));
        }
    }
}
