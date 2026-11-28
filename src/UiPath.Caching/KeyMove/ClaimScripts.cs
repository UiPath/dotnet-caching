namespace UiPath.Caching.KeyMove;

/// <summary>The five claim scripts of the move, each the shared header and its body, as written there.</summary>
internal static class ClaimScripts
{
    public const string Pend = Header + "\n" + PendBody;
    public const string Open = Header + "\n" + OpenBody;
    public const string Release = Header + "\n" + ReleaseBody;
    public const string Reopen = Header + "\n" + ReopenBody;
    public const string Drop = Header + "\n" + DropBody;

    private const string Header = """
        -- shared header
        local KINDS = { rt = true, fm = true, im = true, imd = true }
        local STATUS = { pend = true, open = true, closed = true, idle = true }
        local GROUP = '^[%w%._:>@+_%-]+$'
        local function validgroup(g) return #g >= 1 and #g <= 200 and string.match(g, GROUP) ~= nil end
        -- validfield: kind, group for a move field; 'prv', group; 'cur', 'fm'; else nil.
        local function validfield(f)
          if #f > 204 then return nil end
          local k, g = string.match(f, '^(%a+):(.+)$')
          if not k or not validgroup(g) then return nil end
          if KINDS[k] or k == 'prv' then return k, g end
          if k == 'cur' and g == 'fm' then return k, g end
          return nil
        end
        local function ismove(k) return k ~= nil and k ~= 'cur' and k ~= 'prv' end
        local function validid(i) return #i >= 1 and #i <= 8 and string.match(i, '^%w+$') ~= nil end
        local function validdesc(d)
          if #d == 0 or #d > 40 then return false end
          local a, b = string.match(d, '^fmt:(%w+)>(%w+)$')
          if a then return validid(a) and validid(b) end
          return d == 'inst' or d == 'rt'
        end
        local function validtoken(t) return #t == 32 and string.match(t, '^%x+$') ~= nil end
        local function validep(e) return #e >= 1 and #e <= 18 and string.match(e, '^%d+$') ~= nil end
        local function validcert(c)
          if c == '' then return true end
          local e, tm = string.match(c, '^(%d+):(S[05])$')
          return e ~= nil and #e <= 18
        end
        -- parse: nil (absent), false (malformed) or status, token, descriptor, cert.
        local function parse(v)
          if not v then return nil end
          local s, t, d, c = string.match(v, '^(%a+)|(%x+)|([^|]+)|([^|]*)$')
          if (not s) or (not STATUS[s]) or (not validtoken(t)) or (not validdesc(d)) or (not validcert(c)) then return false end
          return s, t, d, c
        end
        -- parseptr: nil (absent), false (malformed), or group, token, toId, fromId; '-' is the empty pointer.
        local function parseptr(v)
          if not v then return nil end
          if v == '-' then return '-', '', '', '' end
          local g, t, a, b = string.match(v, '^([^|]+)|(%x+)|(%w+)|(%w+)$')
          if (not g) or (not validgroup(g)) or (not validtoken(t)) or (not validid(a)) or (not validid(b)) then return false end
          return g, t, a, b
        end
        local function norm(k) if k == 'imd' then return 'im' end return k end
        -- touch: the "already done" branches (return 2) rewrite the field with its current value, so the effect is replicated again
        -- and the caller's WAIT after a repeat confirms replication of the original write (a no-write call would make WAIT vacuous).
        local function touch(f, s, t, d, c) redis.call('HSET', KEYS[1], f, s .. '|' .. t .. '|' .. d .. '|' .. c) end
        -- excl: 0 ok, -4 malformed field or another kind live, -3 same single-owner kind, other group, live.
        local function excl(key, field)
          local k, g = validfield(field)
          k = norm(k)
          local all = redis.call('HKEYS', key)
          for _, f in ipairs(all) do
            local fk = validfield(f)
            if not fk then return -4 end
            local v = redis.call('HGET', key, f)
            if ismove(fk) then
              if parse(v) == false then return -4 end
            elseif parseptr(v) == false then return -4 end
          end
          local same = false
          for _, f in ipairs(all) do
            if f ~= field then
              local fk, fg = validfield(f)
              if ismove(fk) then
                local s = parse(redis.call('HGET', key, f))
                if s == 'pend' or s == 'open' then
                  if norm(fk) ~= k then return -4 end
                  if k ~= 'rt' and fg ~= g then same = true end
                end
              end
            end
          end
          if same then return -3 end
          return 0
        end
        """;

    private const string PendBody = """
        -- CLAIMPEND. KEYS 1 claim. ARGV 1 field, 2 token, 3 descriptor, 4 expected pointer (for fm: the exact `cur:fm` value the gate read, or '-' when it was absent; for every other kind: '').
        local fk0 = validfield(ARGV[1])
        if (not ismove(fk0)) or (not validtoken(ARGV[2])) or (not validdesc(ARGV[3])) then return -8 end
        if fk0 == 'fm' then
          if ARGV[4] == '' or (ARGV[4] ~= '-' and parseptr(ARGV[4]) == false) then return -8 end
        elseif ARGV[4] ~= '' then return -8 end
        local s, t, d, c = parse(redis.call('HGET', KEYS[1], ARGV[1]))
        if s == false then return -9 end
        if s == 'pend' or s == 'open' then
          if t == ARGV[2] then touch(ARGV[1], s, t, d, c) return 2 end
          return 0
        end
        if s == 'closed' then return 0 end
        if s and t == ARGV[2] then return -6 end
        if fk0 == 'fm' then
          local curp = redis.call('HGET', KEYS[1], 'cur:fm') or '-'
          if curp ~= ARGV[4] then return -5 end
        end
        local x = excl(KEYS[1], ARGV[1])
        if x ~= 0 then return x end
        local k, g = validfield(ARGV[1])
        k = norm(k)
        if k ~= 'rt' then
          for _, f in ipairs(redis.call('HKEYS', KEYS[1])) do
            local fk, fg = validfield(f)
            if ismove(fk) and norm(fk) == k and fg ~= g and parse(redis.call('HGET', KEYS[1], f)) == 'idle' then
              redis.call('HDEL', KEYS[1], f)
            end
          end
        end
        redis.call('HSET', KEYS[1], ARGV[1], 'pend|' .. ARGV[2] .. '|' .. ARGV[3] .. '|')
        return 1
        """;

    private const string OpenBody = """
        -- CLAIMOPEN. KEYS 1 claim. ARGV 1 field, 2 token, 3 descriptor, 4 restore ('0' or '1').
        if (not ismove((validfield(ARGV[1])))) or (not validtoken(ARGV[2])) or (not validdesc(ARGV[3])) then return -8 end
        local s, t, d, c = parse(redis.call('HGET', KEYS[1], ARGV[1]))
        if s == false then return -9 end
        if s and t == ARGV[2] then
          if s == 'open' then touch(ARGV[1], s, t, d, c) return 2 end
          if s == 'pend' then
            redis.call('HSET', KEYS[1], ARGV[1], 'open|' .. t .. '|' .. d .. '|' .. c)
            return 1
          end
          return 0
        end
        if s == 'pend' or s == 'open' or s == 'closed' then return 0 end
        if ARGV[4] ~= '1' then return 0 end
        local x = excl(KEYS[1], ARGV[1])
        if x ~= 0 then return x end
        redis.call('HSET', KEYS[1], ARGV[1], 'open|' .. ARGV[2] .. '|' .. ARGV[3] .. '|')
        return 1
        """;

    private const string ReleaseBody = $$"""
        -- CLAIMREL. KEYS 1 claim. ARGV 1 field, 2 token, 3 target ('cert', 'idle', 'closed'), 4 ep (cert only, else ''), 5 terminal ('{{KeyMoveStates.Off}}' or '{{KeyMoveStates.Done}}', cert only, else '').
        local tg = ARGV[3]
        local fk0, fg0 = validfield(ARGV[1])
        if (not ismove(fk0)) or (not validtoken(ARGV[2])) or (tg ~= 'cert' and tg ~= 'idle' and tg ~= 'closed') then return -8 end
        if tg == 'cert' then
          if (not validep(ARGV[4])) or (ARGV[5] ~= '{{KeyMoveStates.Off}}' and ARGV[5] ~= '{{KeyMoveStates.Done}}') then return -8 end
        elseif ARGV[4] ~= '' or ARGV[5] ~= '' then return -8 end
        local s, t, d, c = parse(redis.call('HGET', KEYS[1], ARGV[1]))
        if s == false then return -9 end
        if (not s) or t ~= ARGV[2] then return 0 end
        if tg == 'cert' then
          local nc = ARGV[4] .. ':' .. ARGV[5]
          if s == 'open' then
            if c == nc then touch(ARGV[1], s, t, d, c) return 2 end
            redis.call('HSET', KEYS[1], ARGV[1], 'open|' .. t .. '|' .. d .. '|' .. nc)
            return 1
          end
          if (s == 'idle' or s == 'closed') and c == nc then touch(ARGV[1], s, t, d, c) return 2 end
          return 0
        end
        if s == tg then touch(ARGV[1], s, t, d, c) return 2 end
        if tg == 'idle' then
          if s == 'pend' then
            redis.call('HSET', KEYS[1], ARGV[1], 'idle|' .. t .. '|' .. d .. '|')
            return 1
          end
          if s == 'open' and string.match(c, ':{{KeyMoveStates.Off}}$') then
            if fk0 == 'fm' then
              local cg = parseptr(redis.call('HGET', KEYS[1], 'cur:fm'))
              if cg == false then return -9 end
              if cg == fg0 then
                local pv = redis.call('HGET', KEYS[1], 'prv:' .. fg0)
                if pv and parseptr(pv) == false then return -9 end
                if (not pv) or pv == '-' then redis.call('HDEL', KEYS[1], 'cur:fm') else redis.call('HSET', KEYS[1], 'cur:fm', pv) end
                redis.call('HDEL', KEYS[1], 'prv:' .. fg0)
              end
            end
            redis.call('HSET', KEYS[1], ARGV[1], 'idle|' .. t .. '|' .. d .. '|' .. c)
            return 1
          end
          return 0
        end
        if s == 'open' and string.match(c, ':{{KeyMoveStates.Done}}$') then
          if fk0 == 'fm' then
            local old = redis.call('HGET', KEYS[1], 'cur:fm')
            local cg = parseptr(old)
            if cg == false then return -9 end
            if cg ~= fg0 then
              local a, b = string.match(d, '^fmt:(%w+)>(%w+)$')
              if not a then return -8 end
              redis.call('HSET', KEYS[1], 'prv:' .. fg0, old or '-')
              redis.call('HSET', KEYS[1], 'cur:fm', fg0 .. '|' .. t .. '|' .. b .. '|' .. a)
            end
          end
          redis.call('HSET', KEYS[1], ARGV[1], 'closed|' .. t .. '|' .. d .. '|' .. c)
          return 1
        end
        return 0
        """;

    private const string ReopenBody = """
        -- CLAIMREOPEN (X11 begin, for fm and rt groups; the same script for both; for fm only the group the pointer names may be reopened, model MC3). KEYS 1 claim. ARGV 1 field, 2 token.
        local fk0, fg0 = validfield(ARGV[1])
        if (not ismove(fk0)) or (not validtoken(ARGV[2])) then return -8 end
        local s, t, d, c = parse(redis.call('HGET', KEYS[1], ARGV[1]))
        if s == false then return -9 end
        if (not s) or t ~= ARGV[2] then return 0 end
        if s == 'open' then touch(ARGV[1], s, t, d, c) return 2 end
        if s ~= 'closed' then return 0 end
        if fk0 == 'fm' then
          local cg = parseptr(redis.call('HGET', KEYS[1], 'cur:fm'))
          if cg == false then return -9 end
          if cg ~= fg0 then return 0 end
        end
        local x = excl(KEYS[1], ARGV[1])
        if x ~= 0 then return x end
        redis.call('HSET', KEYS[1], ARGV[1], 'open|' .. t .. '|' .. d .. '|')
        return 1
        """;

    private const string DropBody = """
        -- CLAIMDROP (the fenced operator delete behind ReleaseClaim and RemoveGroup). KEYS 1 claim. ARGV 1 field, 2 the exact expected value, 3 confirm nonce (non-empty).
        if ARGV[3] == '' or ARGV[1] == 'cur:fm' or (not validfield(ARGV[1])) or #ARGV[2] > 400 then return -8 end
        local cur = redis.call('HGET', KEYS[1], ARGV[1])
        if not cur then return 2 end
        if cur ~= ARGV[2] then return 0 end
        redis.call('HDEL', KEYS[1], ARGV[1])
        return 1
        """;
}
