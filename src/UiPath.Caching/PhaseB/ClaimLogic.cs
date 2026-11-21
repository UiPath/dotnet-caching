namespace UiPath.Caching.PhaseB;

/// <summary>The five claim scripts as C# predicates over one snapshot, statement for statement, returning the code and the writes in order.</summary>
internal static class ClaimLogic
{
    public static ClaimDecision Pend(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token, string descriptor, string expectedPointer)
    {
        var view = new Snapshot(record);
        var fk0 = ClaimGrammar.ValidField(field)?.Kind;
        if (!ClaimGrammar.IsMove(fk0) || !ClaimGrammar.ValidToken(token) || !ClaimGrammar.ValidDescriptor(descriptor))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        if (fk0 == "fm")
        {
            if (expectedPointer.Length == 0 || (expectedPointer != ClaimGrammar.NoPointer && ClaimGrammar.ParsePointer(expectedPointer)!.IsMalformed))
            {
                return ClaimDecision.Of(ClaimCodes.BadArgument);
            }
        }
        else if (expectedPointer.Length != 0)
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var own = ClaimGrammar.Parse(view.Get(field));
        if (own is { IsMalformed: true })
        {
            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
        }

        if (own is { IsLive: true })
        {
            return own.Token == token ? ClaimDecision.Touch(field, own) : ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (own is { Status: ClaimGrammar.Closed })
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (own is not null && own.Token == token)
        {
            return ClaimDecision.Of(ClaimCodes.TokenSpent);
        }

        if (fk0 == "fm" && (view.Get(ClaimGrammar.Pointer) ?? ClaimGrammar.NoPointer) != expectedPointer)
        {
            return ClaimDecision.Of(ClaimCodes.PointerChanged);
        }

        var x = Exclusion(view, field);
        if (x != 0)
        {
            return ClaimDecision.Of(x);
        }

        var (kind, group) = ClaimGrammar.ValidField(field)!.Value;
        kind = ClaimGrammar.Normalize(kind);
        var writes = new List<ClaimWrite>();
        if (kind != "rt")
        {
            foreach (var (f, v) in view.Entries)
            {
                if (ClaimGrammar.ValidField(f) is { } other && ClaimGrammar.IsMove(other.Kind) && ClaimGrammar.Normalize(other.Kind) == kind && other.Group != group
                    && ClaimGrammar.Parse(v) is { IsMalformed: false, Status: ClaimGrammar.Idle })
                {
                    writes.Add(ClaimWrite.Delete(f));
                }
            }
        }

        writes.Add(ClaimWrite.Set(field, $"{ClaimGrammar.Pending}|{token}|{descriptor}|"));
        return new ClaimDecision(ClaimCodes.Done, writes);
    }

    public static ClaimDecision Open(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token, string descriptor, bool restore)
    {
        var view = new Snapshot(record);
        if (!ClaimGrammar.IsMove(ClaimGrammar.ValidField(field)?.Kind) || !ClaimGrammar.ValidToken(token) || !ClaimGrammar.ValidDescriptor(descriptor))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var own = ClaimGrammar.Parse(view.Get(field));
        if (own is { IsMalformed: true })
        {
            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
        }

        if (own is not null && own.Token == token)
        {
            return own.Status switch
            {
                ClaimGrammar.Open => ClaimDecision.Touch(field, own),
                ClaimGrammar.Pending => ClaimDecision.One(field, $"{ClaimGrammar.Open}|{own.Token}|{own.Descriptor}|{own.Cert}"),
                _ => ClaimDecision.Of(ClaimCodes.Refused),
            };
        }

        if (own is { Status: ClaimGrammar.Pending or ClaimGrammar.Open or ClaimGrammar.Closed } || !restore)
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        var x = Exclusion(view, field);
        return x != 0 ? ClaimDecision.Of(x) : ClaimDecision.One(field, $"{ClaimGrammar.Open}|{token}|{descriptor}|");
    }

    /// <param name="target"><c>cert</c>, <c>idle</c> or <c>closed</c>.</param>
    /// <param name="epoch">For <c>cert</c> only; otherwise empty.</param>
    /// <param name="terminal"><c>S0</c> or <c>S5</c> for <c>cert</c> only; otherwise empty.</param>
    public static ClaimDecision Release(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token, string target, string epoch, string terminal)
    {
        var view = new Snapshot(record);
        var parsed = ClaimGrammar.ValidField(field);
        if (!ClaimGrammar.IsMove(parsed?.Kind) || !ClaimGrammar.ValidToken(token) || target is not ("cert" or ClaimGrammar.Idle or ClaimGrammar.Closed))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        if (target == "cert")
        {
            if (!ClaimGrammar.ValidEpoch(epoch) || terminal is not ("S0" or "S5"))
            {
                return ClaimDecision.Of(ClaimCodes.BadArgument);
            }
        }
        else if (epoch.Length != 0 || terminal.Length != 0)
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var (fk0, fg0) = parsed!.Value;
        var own = ClaimGrammar.Parse(view.Get(field));
        if (own is { IsMalformed: true })
        {
            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
        }

        if (own is null || own.Token != token)
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (target == "cert")
        {
            var cert = $"{epoch}:{terminal}";
            if (own.Status == ClaimGrammar.Open)
            {
                return own.Cert == cert ? ClaimDecision.Touch(field, own) : ClaimDecision.One(field, $"{ClaimGrammar.Open}|{own.Token}|{own.Descriptor}|{cert}");
            }

            return own.Status is ClaimGrammar.Idle or ClaimGrammar.Closed && own.Cert == cert ? ClaimDecision.Touch(field, own) : ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (own.Status == target)
        {
            return ClaimDecision.Touch(field, own);
        }

        if (target == ClaimGrammar.Idle)
        {
            if (own.Status == ClaimGrammar.Pending)
            {
                return ClaimDecision.One(field, $"{ClaimGrammar.Idle}|{own.Token}|{own.Descriptor}|");
            }

            if (own.Status == ClaimGrammar.Open && own.Cert.EndsWith(":S0", StringComparison.Ordinal))
            {
                var writes = new List<ClaimWrite>();
                if (fk0 == "fm")
                {
                    var pointer = ClaimGrammar.ParsePointer(view.Get(ClaimGrammar.Pointer));
                    if (pointer is { IsMalformed: true })
                    {
                        return ClaimDecision.Of(ClaimCodes.OwnMalformed);
                    }

                    if (pointer is not null && pointer.Group == fg0)
                    {
                        var previous = view.Get("prv:" + fg0);
                        if (previous is not null && ClaimGrammar.ParsePointer(previous)!.IsMalformed)
                        {
                            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
                        }

                        writes.Add(previous is null or ClaimGrammar.NoPointer ? ClaimWrite.Delete(ClaimGrammar.Pointer) : ClaimWrite.Set(ClaimGrammar.Pointer, previous));
                        writes.Add(ClaimWrite.Delete("prv:" + fg0));
                    }
                }

                writes.Add(ClaimWrite.Set(field, $"{ClaimGrammar.Idle}|{own.Token}|{own.Descriptor}|{own.Cert}"));
                return new ClaimDecision(ClaimCodes.Done, writes);
            }

            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (own.Status == ClaimGrammar.Open && own.Cert.EndsWith(":S5", StringComparison.Ordinal))
        {
            var writes = new List<ClaimWrite>();
            if (fk0 == "fm")
            {
                var old = view.Get(ClaimGrammar.Pointer);
                var pointer = ClaimGrammar.ParsePointer(old);
                if (pointer is { IsMalformed: true })
                {
                    return ClaimDecision.Of(ClaimCodes.OwnMalformed);
                }

                if (pointer is null || pointer.Group != fg0)
                {
                    if (!TryFormatIds(own.Descriptor, out var fromId, out var toId))
                    {
                        return ClaimDecision.Of(ClaimCodes.BadArgument);
                    }

                    writes.Add(ClaimWrite.Set("prv:" + fg0, old ?? ClaimGrammar.NoPointer));
                    writes.Add(ClaimWrite.Set(ClaimGrammar.Pointer, $"{fg0}|{own.Token}|{toId}|{fromId}"));
                }
            }

            writes.Add(ClaimWrite.Set(field, $"{ClaimGrammar.Closed}|{own.Token}|{own.Descriptor}|{own.Cert}"));
            return new ClaimDecision(ClaimCodes.Done, writes);
        }

        return ClaimDecision.Of(ClaimCodes.Refused);
    }

    public static ClaimDecision Reopen(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token)
    {
        var view = new Snapshot(record);
        var parsed = ClaimGrammar.ValidField(field);
        if (!ClaimGrammar.IsMove(parsed?.Kind) || !ClaimGrammar.ValidToken(token))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var (fk0, fg0) = parsed!.Value;
        var own = ClaimGrammar.Parse(view.Get(field));
        if (own is { IsMalformed: true })
        {
            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
        }

        if (own is null || own.Token != token)
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (own.Status == ClaimGrammar.Open)
        {
            return ClaimDecision.Touch(field, own);
        }

        if (own.Status != ClaimGrammar.Closed)
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (fk0 == "fm")
        {
            var pointer = ClaimGrammar.ParsePointer(view.Get(ClaimGrammar.Pointer));
            if (pointer is { IsMalformed: true })
            {
                return ClaimDecision.Of(ClaimCodes.OwnMalformed);
            }

            if (pointer is null || pointer.Group != fg0)
            {
                return ClaimDecision.Of(ClaimCodes.Refused);
            }
        }

        var x = Exclusion(view, field);
        return x != 0 ? ClaimDecision.Of(x) : ClaimDecision.One(field, $"{ClaimGrammar.Open}|{own.Token}|{own.Descriptor}|");
    }

    public static ClaimDecision Drop(IReadOnlyList<KeyValuePair<string, string>> record, string field, string expected, string confirm)
    {
        if (confirm.Length == 0 || field == ClaimGrammar.Pointer || ClaimGrammar.ValidField(field) is null || ClaimGrammar.Bytes(expected) > 400)
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var current = new Snapshot(record).Get(field);
        if (current is null)
        {
            return ClaimDecision.Of(ClaimCodes.AlreadyDone);
        }

        return current != expected ? ClaimDecision.Of(ClaimCodes.Refused) : new ClaimDecision(ClaimCodes.Done, [ClaimWrite.Delete(field)]);
    }

    /// <summary>The scripts' <c>excl</c>: pass 1 refuses any malformed name or value (-4); pass 2 another live kind (-4), then another live group of a single-owner kind (-3).</summary>
    private static long Exclusion(Snapshot view, string field)
    {
        var (ownKind, ownGroup) = ClaimGrammar.ValidField(field)!.Value;
        ownKind = ClaimGrammar.Normalize(ownKind);
        foreach (var (f, v) in view.Entries)
        {
            if (ClaimGrammar.ValidField(f) is not { } parsed)
            {
                return ClaimCodes.OtherMove;
            }

            var malformed = ClaimGrammar.IsMove(parsed.Kind) ? ClaimGrammar.Parse(v)!.IsMalformed : ClaimGrammar.ParsePointer(v)!.IsMalformed;
            if (malformed)
            {
                return ClaimCodes.OtherMove;
            }
        }

        var same = false;
        foreach (var (f, v) in view.Entries)
        {
            if (f == field)
            {
                continue;
            }

            var (kind, group) = ClaimGrammar.ValidField(f)!.Value;
            if (ClaimGrammar.IsMove(kind) && ClaimGrammar.Parse(v) is { IsLive: true })
            {
                if (ClaimGrammar.Normalize(kind) != ownKind)
                {
                    return ClaimCodes.OtherMove;
                }

                if (ownKind != "rt" && group != ownGroup)
                {
                    same = true;
                }
            }
        }

        return same ? ClaimCodes.OtherGroup : 0;
    }

    private static bool TryFormatIds(string descriptor, out string fromId, out string toId)
    {
        fromId = toId = string.Empty;
        if (!descriptor.StartsWith("fmt:", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = descriptor[4..];
        var arrow = rest.IndexOf('>', StringComparison.Ordinal);
        if (arrow <= 0 || arrow == rest.Length - 1)
        {
            return false;
        }

        (fromId, toId) = (rest[..arrow], rest[(arrow + 1)..]);
        return fromId.All(ClaimGrammar.IsAlnum) && toId.All(ClaimGrammar.IsAlnum);
    }

    /// <summary>The record as the script sees it: lookups by field, entries in the server's order.</summary>
    private sealed class Snapshot(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        private readonly Dictionary<string, string> _byField = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        public IReadOnlyList<KeyValuePair<string, string>> Entries { get; } = entries;

        public string? Get(string field) => _byField.TryGetValue(field, out var value) ? value : null;
    }
}
