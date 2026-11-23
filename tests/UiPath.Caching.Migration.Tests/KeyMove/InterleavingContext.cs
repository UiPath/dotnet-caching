using UiPath.Caching.Migration.Tests.KeyMove.Harness;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The actors of a scenario: each call is one operation of the real client, recorded in the oracle at its start and its end.
internal sealed class InterleavingContext(KeyMoveKit kit, HistoryOracle oracle, List<string> violations)
{
    private const string Key = "k";
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private int _created;

    public int Created => _created;

    public Func<Task> Read() => async () =>
    {
        int id;
        lock (_gate)
        {
            id = oracle.BeginRead(0);
        }

        var read = await kit.Client.ReadAsync(Key, CancellationToken.None);
        lock (_gate)
        {
            if (read.Kind is ReadOutcome.Found or ReadOutcome.Absent)
            {
                Report(oracle.EndRead(0, id, read.Kind == ReadOutcome.Found ? read.Value!.Name : null, "fwd", false));
            }
            else
            {
                // No scenario injects a fault, so an answer that is not an answer is a failure of the implementation.
                oracle.DropRead(0, id);
                violations.Add($"a read answered {read.Kind} in a scenario with no fault");
            }
        }
    };

    public Func<Task> Write(string value) => async () =>
    {
        var mutation = Begin(value);
        End(mutation, (await kit.Client.SetAsync(Key, new Item(value), null, CancellationToken.None)).Outcome, strict: true);
    };

    public Func<Task> Delete() => async () =>
    {
        var mutation = Begin(null);
        End(mutation, (await kit.Client.RemoveAsync(Key, CancellationToken.None)).Outcome, strict: true);
    };

    public Func<Task> Fill(string value) => async () =>
    {
        var mutation = Begin(value);
        End(mutation, (await kit.Client.FillAsync(Key, new Item(value), null, new FillBox<Item>(), CancellationToken.None)).Outcome);
    };

    public Func<Task> TryAdd(string value) => async () =>
    {
        var mutation = Begin(value);
        var result = await kit.Client.TryAddAsync(Key, new Item(value), null, CancellationToken.None);
        lock (_gate)
        {
            if (result.Kind == TryAddKind.Created)
            {
                _created++;
                oracle.EndMutation(0, mutation);
            }
            else
            {
                oracle.AbortMutation(0, mutation);
                if (result.Kind == TryAddKind.Failed)
                {
                    violations.Add("a conditional create failed in a scenario with no fault");
                }
            }
        }
    };

    private int Begin(string? value)
    {
        lock (_gate)
        {
            return oracle.BeginMutation(0, value);
        }
    }

    private void End(int mutation, WriteOutcome outcome, bool strict = false)
    {
        lock (_gate)
        {
            if (outcome == WriteOutcome.Committed)
            {
                oracle.EndMutation(0, mutation);
            }
            else
            {
                oracle.AbortMutation(0, mutation);
                if (strict)
                {
                    violations.Add($"a write answered {outcome} in a scenario with no fault");
                }
            }
        }
    }

    private void Report(OracleViolation? violation)
    {
        if (violation is not null)
        {
            violations.Add(violation.Message);
        }
    }
}
