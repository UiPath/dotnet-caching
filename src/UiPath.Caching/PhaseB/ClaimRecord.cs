namespace UiPath.Caching.PhaseB;

/// <summary>The claim as admission reads it: every move field parsed, the current format pointer, and whether anything in the record is malformed.</summary>
internal sealed class ClaimRecord
{
    private ClaimRecord(IReadOnlyDictionary<string, ClaimValue> moves, ClaimPointer? pointer, bool malformed, IReadOnlyList<KeyValuePair<string, string>> raw)
    {
        Moves = moves;
        Pointer = pointer;
        Malformed = malformed;
        Raw = raw;
    }

    /// <summary>Move fields by name; a malformed one is left out and sets <see cref="Malformed"/>.</summary>
    public IReadOnlyDictionary<string, ClaimValue> Moves { get; }

    /// <summary>The permanent current format pointer; null when absent.</summary>
    public ClaimPointer? Pointer { get; }

    /// <summary>A field name outside the grammar or a value that does not parse: admission refuses with <c>claim_malformed</c>.</summary>
    public bool Malformed { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Raw { get; }

    public IEnumerable<KeyValuePair<string, ClaimValue>> Live => Moves.Where(m => m.Value.IsLive);

    public static ClaimRecord From(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var moves = new Dictionary<string, ClaimValue>(StringComparer.Ordinal);
        ClaimPointer? pointer = null;
        var malformed = false;
        foreach (var (field, value) in entries)
        {
            if (ClaimGrammar.ValidField(field) is not { } parsed)
            {
                malformed = true;
            }
            else if (ClaimGrammar.IsMove(parsed.Kind))
            {
                var move = ClaimGrammar.Parse(value)!;
                if (move.IsMalformed)
                {
                    malformed = true;
                }
                else
                {
                    moves[field] = move;
                }
            }
            else
            {
                var ptr = ClaimGrammar.ParsePointer(value)!;
                malformed |= ptr.IsMalformed;
                if (field == ClaimGrammar.Pointer && !ptr.IsMalformed)
                {
                    pointer = ptr;
                }
            }
        }

        return new ClaimRecord(moves, pointer, malformed, entries);
    }

    public string? ValueOf(string field) => Raw.FirstOrDefault(e => e.Key == field).Value;
}
