using System.Globalization;
using System.Text.RegularExpressions;

namespace UiPath.Caching.Tests.KeyMove.Harness;

// A C# model of each key move script: the same argument order, return codes and check order as the Lua in MoveScripts*.cs.
// A conformance test runs a shared table of scenarios against this model and against the real scripts.
internal static partial class ModelScripts
{
    // markerField empty: no marker is looked at. A marker counts while it starts with the record's binding value and a bar.
    public static CapResult Cap(ShardStore s, long now, string fence, string data, string ctl, long fenceTtl, string nonce, string id, string cid, ModelFaults f, string markerField = "") =>
        s.Scripted(() =>
        {
            if (!IdentityHolds(s, ctl, id, f))
            {
                return new CapResult(-1, null, false, null, 0);
            }

            var m = s.HGet(ctl, "c:" + cid);
            if (m is null && f.AbsentRecordIsOpen && !s.Exists(ctl))
            {
                m = "OPEN:absent";
            }

            if (m is null || m.StartsWith("CLOSED", StringComparison.Ordinal))
            {
                return new CapResult(-2, null, false, null, 0);
            }

            if (markerField.Length > 0 && s.HGet(ctl, markerField) is { } sm && SmLive(sm, Binding(s, ctl)))
            {
                return new CapResult(-4, null, false, null, 0, sm);
            }

            var g = s.GetString(fence);
            if (g is null)
            {
                g = f.CounterTokens ? "1" : nonce;
                s.Put(fence, Entry.Str(g));
            }

            s.ExpireAt(fence, now + fenceTtl, now);
            return new CapResult(0, g, s.Exists(data), m, now);
        });

    public static long Pub(ShardStore s, long now, string fence, string data, string ctl, string token, string value, bool hash, long px, string mode, string gateSeen, string id, string cid, string newNonce, long fenceTtl, long tcap, ModelFaults f) =>
        s.Scripted(() =>
        {
            var refusal = PublishChecks(s, ctl, fence, data, token, mode, gateSeen, id, cid, f);
            if (refusal is { } code)
            {
                return code;
            }

            long? deadline = null;
            if (px != -1 && tcap != -1)
            {
                deadline = tcap + px;
                if (deadline.Value - now < 1)
                {
                    return -3;
                }
            }

            s.Put(data, Entry.OfVersion(value, hash));
            if (deadline is { } at)
            {
                s.ExpireAt(data, at, now);
            }

            if (tcap == -1 && px != -1)
            {
                s.ExpireAt(data, now + px, now);
            }

            ReplaceFence(s, now, fence, newNonce, fenceTtl, f);
            return 1;
        });

    public static long PubHash(ShardStore s, long now, string fence, string data, string staging, string ctl, string token, long px, string mode, string gateSeen, string id, string cid, string newNonce, long fenceTtl, long tcap, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!s.Exists(staging))
            {
                return 0;
            }

            var refusal = PublishChecks(s, ctl, fence, data, token, mode, gateSeen, id, cid, f);
            if (refusal is { } code)
            {
                s.Remove(staging);
                return code;
            }

            long? deadline = null;
            if (px != -1)
            {
                deadline = tcap + px;
                if (deadline.Value - now < 1)
                {
                    s.Remove(staging);
                    return -3;
                }
            }

            s.Put(data, s.Get(staging)!);
            s.Remove(staging);
            if (deadline is { } at)
            {
                s.ExpireAt(data, at, now);
            }
            else
            {
                s.Persist(data);
            }

            ReplaceFence(s, now, fence, newNonce, fenceTtl, f);
            return 1;
        });

    // op: "set", "del" or "none"; del removes the marker field when one is given. A marked set goes through BumpMarked.
    public static long Bump(ShardStore s, long now, string fence, string data, string ctl, long fenceTtl, string op, string value, bool hash, long px, string nonce, string id, ModelFaults f, string markerField = "") =>
        BumpMarked(s, now, fence, data, ctl, fenceTtl, op, value, hash, px, nonce, id, f, markerField, false, 0).Code;

    // setm is set plus the marker. -7 late (now is past the deadline, 0 for none), -8 no binding value, -6 a first attempt at a record whose field count is at its lim: each changes nothing. Else 1 with the field count and the marker written.
    public static (long Code, long Length, string? Marker) BumpMarked(ShardStore s, long now, string fence, string data, string ctl, long fenceTtl, string op, string value, bool hash, long px, string nonce, string id, ModelFaults f, string markerField, bool first, long deadline) =>
        s.Scripted<(long, long, string?)>(() =>
        {
            if (!IdentityHolds(s, ctl, id, f))
            {
                return (-1, 0, null);
            }

            string? b = null;
            if (op == "setm")
            {
                if (deadline != 0 && now > deadline)
                {
                    return (-7, 0, null);
                }

                b = Binding(s, ctl);
                if (string.IsNullOrEmpty(b))
                {
                    return (-8, 0, null);
                }

                if (first && s.HGet(ctl, markerField) is null && FieldCount(s, ctl) >= long.Parse(s.HGet(ctl, "lim") ?? "0", CultureInfo.InvariantCulture))
                {
                    return (-6, 0, null);
                }
            }

            ReplaceFence(s, now, fence, nonce, fenceTtl, f);
            switch (op)
            {
                case "none":
                    return (1, 0, null);
                case "del":
                    if (markerField.Length > 0)
                    {
                        s.HDel(ctl, markerField);
                    }

                    return (s.Remove(data) ? 1 : 0, 0, null);
                default:
                    s.Put(data, Entry.OfVersion(value, hash));
                    if (px != -1)
                    {
                        s.ExpireAt(data, now + px, now);
                    }

                    if (op != "setm")
                    {
                        return (1, 0, null);
                    }

                    var marker = b + "|" + nonce;
                    s.HSet(ctl, markerField, marker);
                    var n = FieldCount(s, ctl);
                    if (n > long.Parse(s.HGet(ctl, "pk") ?? "0", CultureInfo.InvariantCulture))
                    {
                        s.HSet(ctl, "pk", n.ToString(CultureInfo.InvariantCulture));
                    }

                    return (1, n, marker);
            }
        });

    public static long Unmark(ShardStore s, string ctl, string field, string expected) =>
        s.Scripted(() => s.HGet(ctl, field) == expected && s.HDel(ctl, field) ? 1 : 0);

    public static long UnmarkMany(ShardStore s, string ctl, IReadOnlyList<(string Field, string Expected)> markers) =>
        s.Scripted(() => markers.Sum(m => s.HGet(ctl, m.Field) == m.Expected && s.HDel(ctl, m.Field) ? 1L : 0L));

    public static long Smgc(ShardStore s, string ctl, string field, string expected) =>
        s.Scripted(() =>
        {
            var v = s.HGet(ctl, field);
            if (v != expected)
            {
                return 0;
            }

            var binding = BindingOf(v!);
            var cur = Binding(s, ctl);
            if ((cur is not null && binding == cur) || binding == s.HGet(ctl, "rg"))
            {
                return -1;
            }

            return s.HDel(ctl, field) ? 1 : 0;
        });

    public static long[] SmgcMany(ShardStore s, string ctl, IReadOnlyList<(string Field, string Expected)> markers) =>
        [.. markers.Select(m => Smgc(s, ctl, m.Field, m.Expected))];

    // -1 identity, -9 the binding is neither the record's current one nor its rg, else the delete count.
    public static long SrcDelG(ShardStore s, string key, string ctl, string id, string binding, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            var cur = Binding(s, ctl);
            var rg = s.HGet(ctl, "rg");
            if (binding.Length == 0 || (binding != cur && binding != rg))
            {
                return -9;
            }

            return s.Remove(key) ? 1 : 0;
        });

    public static long LimSet(ShardStore s, string ctl, string id, long limit) =>
        s.Scripted(() =>
        {
            if (s.HGet(ctl, "id") != id)
            {
                return -1;
            }

            s.HSet(ctl, "lim", limit.ToString(CultureInfo.InvariantCulture));
            return 1;
        });

    // The record is rewritten whole (so its table is new) and pk starts again from the length that the rewrite gave.
    public static long Compact(ShardStore s, string ctl, string id) =>
        s.Scripted(() =>
        {
            if (s.HGet(ctl, "id") != id)
            {
                return -1;
            }

            if (FieldCount(s, ctl) > 2048)
            {
                return -2;
            }

            s.HSet(ctl, "pk", FieldCount(s, ctl).ToString(CultureInfo.InvariantCulture));
            return FieldCount(s, ctl);
        });

    public static long Fill(ShardStore s, long now, string fence, string data, string ctl, string value, bool hash, long px, string nonce, long fenceTtl, string id, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!IdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (s.Exists(data))
            {
                return 2;
            }

            s.Put(data, Entry.OfVersion(value, hash));
            if (px != -1)
            {
                s.ExpireAt(data, now + px, now);
            }

            ReplaceFence(s, now, fence, nonce, fenceTtl, f);
            return 1;
        });

    public static long FillHash(ShardStore s, long now, string staging, string data, string fence, string ctl, string id, long px, string nonce, long fenceTtl, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!IdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (!s.Exists(staging))
            {
                return 3;
            }

            if (s.Exists(data))
            {
                return 2;
            }

            s.Put(data, s.Get(staging)!);
            s.Remove(staging);
            if (px == -1)
            {
                s.Persist(data);
            }
            else
            {
                s.ExpireAt(data, now + px, now);
            }

            ReplaceFence(s, now, fence, nonce, fenceTtl, f);
            return 1;
        });

    public static long SrcDel(ShardStore s, string key, string ctl, string id, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            return s.Remove(key) ? 1 : 0;
        });

    public static long SrcSet(ShardStore s, long now, string key, string ctl, string id, string value, bool hash, long px, string mode, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (mode == "nx" && s.Exists(key))
            {
                return 2;
            }

            s.Put(key, Entry.OfVersion(value, hash));
            if (px != -1)
            {
                s.ExpireAt(key, now + px, now);
            }

            return 1;
        });

    public static long SrcExp(ShardStore s, long now, string key, string ctl, string id, long px, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (px == -1)
            {
                if (!s.Exists(key))
                {
                    return 0;
                }

                s.Persist(key);
                return 1;
            }

            return s.ExpireAt(key, now + px, now) ? 1 : 0;
        });

    // A hash key raises WRONGTYPE in the script after the identity check; the client reads that as 0.
    public static long CmpDel(ShardStore s, string key, string ctl, string id, string expected, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            try
            {
                return s.GetString(key) == expected && s.Remove(key) ? 1 : 0;
            }
            catch (WrongTypeException)
            {
                return 0;
            }
        });

    public static long CmpDelHash(ShardStore s, string key, string ctl, string id, IReadOnlyList<KeyValuePair<string, string>> expected, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            var e = s.Get(key);
            if (e is { IsHash: false })
            {
                return 0;
            }

            var held = e?.Fields?.Count ?? 0;
            if (held != expected.Count || expected.Any(p => s.HGet(key, p.Key) != p.Value))
            {
                return 0;
            }

            return s.Remove(key) ? 1 : 0;
        });

    public static long CtlSync(ShardStore s, string ctl, long ep, string id, string st, string dir, string np, string pst, string pdir, string pnp, string pep, string pid, IReadOnlyList<(string Cid, string Nonce)> gates, ModelFaults f, long? limit = null, string bf = "", string fg = "", string rg = "") =>
        s.Scripted(() =>
        {
            var cur = s.HGet(ctl, "ep");
            if (cur is not null)
            {
                var c = long.Parse(cur, CultureInfo.InvariantCulture);
                if (ep < c && !f.CtlSyncLowers)
                {
                    return 0;
                }

                if (ep == c)
                {
                    if (s.HGet(ctl, "id") == id)
                    {
                        s.HSet(ctl, "id", id);
                        return 0;
                    }

                    return -5;
                }
            }

            // The gates to drop are the cache ids of the previous list; a record with no list is scanned.
            var previous = s.HGet(ctl, "cl");
            var old = previous is null
                ? s.Get(ctl)?.Fields?.Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).ToList() ?? []
                : previous.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(c => "c:" + c).ToList();
            var cl = string.Join('+', gates.Select(g => g.Cid).Order(StringComparer.Ordinal));
            foreach (var (field, value) in new[] { ("ep", ep.ToString(CultureInfo.InvariantCulture)), ("id", id), ("st", st), ("dir", dir), ("np", np), ("dur", "0"), ("pst", pst), ("pdir", pdir), ("pnp", pnp), ("pep", pep), ("pid", pid), ("cl", cl) })
            {
                s.HSet(ctl, field, value);
            }

            foreach (var field in old)
            {
                s.HDel(ctl, field);
            }

            if (limit is { } l)
            {
                s.HSet(ctl, "lim", l.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var (field, value) in new[] { ("bf", bf), ("fg", fg), ("rg", rg) })
            {
                if (value.Length > 0)
                {
                    s.HSet(ctl, field, value);
                }
                else
                {
                    s.HDel(ctl, field);
                }
            }

            foreach (var (cid, nonce) in gates)
            {
                s.HSet(ctl, "c:" + cid, "OPEN:" + nonce);
            }

            return 1;
        });

    public static long CtlCommit(ShardStore s, string ctl, long ep, string id) =>
        s.Scripted(() =>
        {
            var cur = s.HGet(ctl, "ep");
            if (cur is not null && long.Parse(cur, CultureInfo.InvariantCulture) == ep)
            {
                if (s.HGet(ctl, "id") != id)
                {
                    return -5;
                }

                s.HSet(ctl, "dur", "1");
                return 1;
            }

            return 0;
        });

    public static long Gate(ShardStore s, string ctl, string id, string cid, string value, string expect) =>
        s.Scripted(() =>
        {
            if (s.HGet(ctl, "id") != id)
            {
                return -1;
            }

            var c = s.HGet(ctl, "c:" + cid);
            if (c == value)
            {
                s.HSet(ctl, "c:" + cid, value);
                return 1;
            }

            if (expect != string.Empty && c != expect)
            {
                return 0;
            }

            s.HSet(ctl, "c:" + cid, value);
            return 1;
        });

    // fenceNonce null: a Src-side hash script, which only checks the control record.
    // HINCRBY fails on a non-integer and past long.MaxValue; the script's pcall leaves the counter as it was and HSETC answers 0.
    public static long HSetC(ShardStore s, long now, string key, string ctl, string id, bool replaceKey, long expireAt, string? dropField, string? versionField, long versionSeed, string? fenceKey, string? fenceNonce, long fenceTtl, IReadOnlyList<KeyValuePair<string, string>> entries, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (fenceKey is not null)
            {
                s.Put(fenceKey, Entry.Str(fenceNonce!, now + fenceTtl));
            }

            if (replaceKey)
            {
                s.Remove(key);
            }

            foreach (var e in entries)
            {
                s.HSet(key, e.Key, e.Value);
            }

            if (dropField is not null)
            {
                s.HDel(key, dropField);
            }

            long result = 1;
            if (versionField is not null)
            {
                if (s.HGet(key, versionField) is null)
                {
                    s.HSet(key, versionField, versionSeed.ToString(CultureInfo.InvariantCulture));
                }

                if (TryRedisInteger(s.HGet(key, versionField), out var n) && n != long.MaxValue)
                {
                    s.HSet(key, versionField, (n + 1).ToString(CultureInfo.InvariantCulture));
                    result = n + 1;
                }
                else
                {
                    result = 0;
                }
            }

            if (expireAt != -1)
            {
                s.ExpireAt(key, expireAt, now);
            }
            else if (!replaceKey)
            {
                s.Persist(key);
            }

            return result;
        });

    public static long HDelC(ShardStore s, long now, string key, string ctl, string id, string? versionField, long versionSeed, string? fenceKey, string? fenceNonce, long fenceTtl, IReadOnlyList<string> fields, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (fenceKey is not null)
            {
                s.Put(fenceKey, Entry.Str(fenceNonce!, now + fenceTtl));
            }

            if (versionField is not null && s.Exists(key))
            {
                if (s.HGet(key, versionField) is null)
                {
                    s.HSet(key, versionField, versionSeed.ToString(CultureInfo.InvariantCulture));
                }

                if (TryRedisInteger(s.HGet(key, versionField), out var n) && n != long.MaxValue)
                {
                    s.HSet(key, versionField, (n + 1).ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (var field in fields)
            {
                s.HDel(key, field);
            }

            if (versionField is not null && s.Get(key) is { IsHash: true } e && e.Fields!.Count == 1 && e.Fields.ContainsKey(versionField))
            {
                s.HDel(key, versionField);
            }

            return 1;
        });

    public static long HExpC(ShardStore s, long now, string key, string ctl, string id, long expireAt, string? fenceKey, string? fenceNonce, long fenceTtl, ModelFaults f) =>
        s.Scripted(() =>
        {
            if (!SourceIdentityHolds(s, ctl, id, f))
            {
                return -1;
            }

            if (fenceKey is not null)
            {
                s.Put(fenceKey, Entry.Str(fenceNonce!, now + fenceTtl));
            }

            if (!s.Exists(key))
            {
                return 0;
            }

            if (expireAt == -1)
            {
                s.Persist(key);
            }
            else
            {
                s.ExpireAt(key, expireAt, now);
            }

            return 1;
        });

    // Redis reads a counter only in canonical form: no sign but '-', no spaces, no leading zero.
    private static bool TryRedisInteger(string? text, out long value)
    {
        value = 0;
        return text is not null && CanonicalInteger().IsMatch(text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex("^(0|-?[1-9][0-9]*)$")]
    private static partial Regex CanonicalInteger();

    // The record's binding value: the field named by bf, or id when it names none.
    private static string? Binding(ShardStore s, string ctl) => s.HGet(ctl, s.HGet(ctl, "bf") ?? "id");

    private static string BindingOf(string marker) => marker.Contains('|', StringComparison.Ordinal) ? marker[..marker.IndexOf('|', StringComparison.Ordinal)] : marker;

    private static bool SmLive(string marker, string? current) => !string.IsNullOrEmpty(current) && marker.StartsWith(current + "|", StringComparison.Ordinal);

    private static long FieldCount(ShardStore s, string ctl) => s.Get(ctl)?.Fields?.Count ?? 0;

    private static bool IdentityHolds(ShardStore s, string ctl, string id, ModelFaults f) =>
        s.HGet(ctl, "id") == id || (f.AbsentRecordIsOpen && !s.Exists(ctl));

    private static bool SourceIdentityHolds(ShardStore s, string ctl, string id, ModelFaults f) =>
        f.SourceScriptsSkipIdentity || IdentityHolds(s, ctl, id, f);

    // The shared prefix of PUB and PUBH: identity, gate, fence token, then the nx check; null when all pass.
    private static long? PublishChecks(ShardStore s, string ctl, string fence, string data, string token, string mode, string gateSeen, string id, string cid, ModelFaults f)
    {
        var absent = f.AbsentRecordIsOpen && !s.Exists(ctl);
        if (!absent && s.HGet(ctl, "id") != id)
        {
            return -1;
        }

        var m = s.HGet(ctl, "c:" + cid);
        if (!absent && (m is null || m != gateSeen || m.StartsWith("CLOSED", StringComparison.Ordinal)))
        {
            return -2;
        }

        var g = s.GetString(fence);
        bool tokenHolds;
        if (f.LengthOnlyToken)
        {
            tokenHolds = g is not null && g.Length == token.Length;
        }
        else if (f.AbsentTokenPasses)
        {
            tokenHolds = g is null || g == token;
        }
        else
        {
            tokenHolds = f.PostPublishRecheck || g == token;
        }
        if (!tokenHolds)
        {
            return 0;
        }

        return mode == "nx" && s.Exists(data) ? 2 : null;
    }

    private static void ReplaceFence(ShardStore s, long now, string fence, string nonce, long ttl, ModelFaults f)
    {
        var value = nonce;
        if (f.CounterTokens)
        {
            value = (long.Parse(s.GetString(fence) ?? "0", CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        }

        s.Put(fence, Entry.Str(value, now + ttl));
    }
}
