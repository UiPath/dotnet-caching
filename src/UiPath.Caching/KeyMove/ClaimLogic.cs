namespace UiPath.Caching.KeyMove;

/// <summary>The five claim scripts as C# predicates over one snapshot, statement for statement, returning the code and the writes in order.</summary>
internal static class ClaimLogic
{
    private const string FormatMove = "fm";
    private const string RouterMove = "rt";
    private const string Certify = "cert";
    private const string PreviousPointerPrefix = "prv:";

    public static ClaimDecision Pend(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token, string descriptor, string expectedPointer)
    {
        var view = new Snapshot(record);
        var fk0 = ClaimGrammar.ValidField(field)?.Kind;
        if (!ClaimGrammar.IsMove(fk0) || !ClaimGrammar.ValidToken(token) || !ClaimGrammar.ValidDescriptor(descriptor))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        if (!ValidExpectedPointer(fk0, expectedPointer))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var own = ClaimGrammar.Parse(view.Get(field));
        if (ExistingPendDecision(field, token, own) is { } existing)
        {
            return existing;
        }

        if (fk0 == FormatMove && (view.Get(ClaimGrammar.Pointer) ?? ClaimGrammar.NoPointer) != expectedPointer)
        {
            return ClaimDecision.Of(ClaimCodes.PointerChanged);
        }

        var x = Exclusion(view, field);
        if (x != 0)
        {
            return ClaimDecision.Of(x);
        }

        return PendWrites(view, field, token, descriptor);
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
    /// <param name="terminal"><c>off</c> or <c>done</c> for <c>cert</c> only; otherwise empty.</param>
    public static ClaimDecision Release(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token, string target, string epoch, string terminal)
    {
        var view = new Snapshot(record);
        var parsed = ClaimGrammar.ValidField(field);
        if (!ClaimGrammar.IsMove(parsed?.Kind) || !ClaimGrammar.ValidToken(token) || target is not (Certify or ClaimGrammar.Idle or ClaimGrammar.Closed))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        if (!ValidReleaseTarget(target, epoch, terminal))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var (fk0, fg0) = parsed.GetValueOrDefault();
        var own = ClaimGrammar.Parse(view.Get(field));
        if (own is { IsMalformed: true })
        {
            return ClaimDecision.Of(ClaimCodes.OwnMalformed);
        }

        if (own is null || own.Token != token)
        {
            return ClaimDecision.Of(ClaimCodes.Refused);
        }

        if (target == Certify)
        {
            return CertifyClaim(field, own, $"{epoch}:{terminal}");
        }

        if (own.Status == target)
        {
            return ClaimDecision.Touch(field, own);
        }

        return target == ClaimGrammar.Idle
            ? ReleaseIdleClaim(view, field, own, fk0, fg0)
            : ReleaseClosedClaim(view, field, own, fk0, fg0);
    }

    public static ClaimDecision Reopen(IReadOnlyList<KeyValuePair<string, string>> record, string field, string token)
    {
        var view = new Snapshot(record);
        var parsed = ClaimGrammar.ValidField(field);
        if (!ClaimGrammar.IsMove(parsed?.Kind) || !ClaimGrammar.ValidToken(token))
        {
            return ClaimDecision.Of(ClaimCodes.BadArgument);
        }

        var (fk0, fg0) = parsed.GetValueOrDefault();
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

        if (fk0 == FormatMove)
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

    private static bool ValidExpectedPointer(string? kind, string expected) =>
        kind == FormatMove
            ? expected.Length != 0 && (expected == ClaimGrammar.NoPointer || ClaimGrammar.ParsePointer(expected) is { IsMalformed: false })
            : expected.Length == 0;

    private static ClaimDecision? ExistingPendDecision(string field, string token, ClaimValue? own)
    {
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

        return null;
    }

    private static ClaimDecision PendWrites(Snapshot view, string field, string token, string descriptor)
    {
        var (kind, group) = ClaimGrammar.ValidField(field).GetValueOrDefault();
        kind = ClaimGrammar.Normalize(kind);
        var writes = new List<ClaimWrite>();
        if (kind != RouterMove)
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

    private static bool ValidReleaseTarget(string target, string epoch, string terminal) =>
        target == Certify
            ? ClaimGrammar.ValidEpoch(epoch) && terminal is KeyMoveStates.Off or KeyMoveStates.Done
            : epoch.Length == 0 && terminal.Length == 0;

    private static ClaimDecision CertifyClaim(string field, ClaimValue own, string cert)
    {
        if (own.Status == ClaimGrammar.Open)
        {
            return own.Cert == cert ? ClaimDecision.Touch(field, own) : ClaimDecision.One(field, $"{ClaimGrammar.Open}|{own.Token}|{own.Descriptor}|{cert}");
        }

        return own.Status is ClaimGrammar.Idle or ClaimGrammar.Closed && own.Cert == cert ? ClaimDecision.Touch(field, own) : ClaimDecision.Of(ClaimCodes.Refused);
    }

    private static ClaimDecision ReleaseIdleClaim(Snapshot view, string field, ClaimValue own, string kind, string group)
    {
        if (own.Status == ClaimGrammar.Pending)
        {
            return ClaimDecision.One(field, $"{ClaimGrammar.Idle}|{own.Token}|{own.Descriptor}|");
        }

        if (own.Status == ClaimGrammar.Open && own.Cert.EndsWith(":" + KeyMoveStates.Off, StringComparison.Ordinal))
        {
            var writes = new List<ClaimWrite>();
            if (kind == FormatMove && !TryRestorePointer(view, group, writes))
            {
                return ClaimDecision.Of(ClaimCodes.OwnMalformed);
            }

            writes.Add(ClaimWrite.Set(field, $"{ClaimGrammar.Idle}|{own.Token}|{own.Descriptor}|{own.Cert}"));
            return new ClaimDecision(ClaimCodes.Done, writes);
        }

        return ClaimDecision.Of(ClaimCodes.Refused);
    }

    private static ClaimDecision ReleaseClosedClaim(Snapshot view, string field, ClaimValue own, string kind, string group)
    {
        if (own.Status == ClaimGrammar.Open && own.Cert.EndsWith(":" + KeyMoveStates.Done, StringComparison.Ordinal))
        {
            var writes = new List<ClaimWrite>();
            if (kind == FormatMove)
            {
                var old = view.Get(ClaimGrammar.Pointer);
                var pointer = ClaimGrammar.ParsePointer(old);
                if (pointer is { IsMalformed: true })
                {
                    return ClaimDecision.Of(ClaimCodes.OwnMalformed);
                }

                if (pointer is null || pointer.Group != group)
                {
                    if (!TryFormatIds(own.Descriptor, out var fromId, out var toId))
                    {
                        return ClaimDecision.Of(ClaimCodes.BadArgument);
                    }

                    writes.Add(ClaimWrite.Set(PreviousPointerPrefix + group, old ?? ClaimGrammar.NoPointer));
                    writes.Add(ClaimWrite.Set(ClaimGrammar.Pointer, $"{group}|{own.Token}|{toId}|{fromId}"));
                }
            }

            writes.Add(ClaimWrite.Set(field, $"{ClaimGrammar.Closed}|{own.Token}|{own.Descriptor}|{own.Cert}"));
            return new ClaimDecision(ClaimCodes.Done, writes);
        }

        return ClaimDecision.Of(ClaimCodes.Refused);
    }

    private static bool TryRestorePointer(Snapshot view, string group, List<ClaimWrite> writes)
    {
        var pointer = ClaimGrammar.ParsePointer(view.Get(ClaimGrammar.Pointer));
        if (pointer is { IsMalformed: true })
        {
            return false;
        }

        if (pointer is not null && pointer.Group == group)
        {
            var previous = view.Get(PreviousPointerPrefix + group);
            if (previous is not null && ClaimGrammar.ParsePointer(previous) is { IsMalformed: true })
            {
                return false;
            }

            writes.Add(previous is null or ClaimGrammar.NoPointer ? ClaimWrite.Delete(ClaimGrammar.Pointer) : ClaimWrite.Set(ClaimGrammar.Pointer, previous));
            writes.Add(ClaimWrite.Delete(PreviousPointerPrefix + group));
        }

        return true;
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

            var malformed = ClaimGrammar.IsMove(parsed.Kind) ? ClaimGrammar.Parse(v).IsMalformed : ClaimGrammar.ParsePointer(v).IsMalformed;
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

                if (ownKind != RouterMove && group != ownGroup)
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
