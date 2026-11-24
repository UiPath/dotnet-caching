using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>
/// The retire gate, passed before the reverse leaves S1: a discovery pass over every slot's control record that finds no marker of the retiring generation. A pass resumes where the last attempt stopped, with an HSCAN cursor,
/// and each attempt stops after <see cref="KeyMoveStateOptions.RetireDiscoveryBudget"/>; a slot whose record holds no more than its fixed fields is skipped by its length.
/// </summary>
internal sealed class RetireGate(IDatabase control, MoveKeyNames names, KeyMoveStateOptions options, TimeProvider time)
{
    private const int ScanPage = 500;
    private const int LengthBatch = 512;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly ConcurrentDictionary<string, Progress> _progress = new(StringComparer.Ordinal);

    public async Task<RetireVerdict> CheckAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        var retiring = state.RetiringGeneration;
        if (state is not { State: "S1", Direction: "rev" } || retiring.Length == 0)
        {
            return RetireVerdict.Done;
        }

        var key = $"{group}\n{state.Identity}";
        foreach (var other in _progress.Keys.Where(k => k.StartsWith(group + "\n", StringComparison.Ordinal) && k != key))
        {
            _progress.TryRemove(other, out _);
        }

        var progress = _progress.GetOrAdd(key, _ => new Progress());
        var slots = names.SlotSet();
        var floor = MarkerSweeper.Floor(state);
        var started = time.GetTimestamp();
        while (progress.Slot < slots.Count)
        {
            var trip = await StepAsync(group, slots, progress, floor, retiring, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (trip && progress.Slot < slots.Count && time.GetElapsedTime(started) >= options.RetireDiscoveryBudget)
            {
                return new RetireVerdict(false, progress.Found, Complete: false, BlockedFor(progress));
            }
        }

        // A whole pass is done: clean, or started again from the first slot.
        var found = progress.Found;
        if (found == 0)
        {
            _progress.TryRemove(key, out _);
            return RetireVerdict.Done;
        }

        progress.BlockedSince ??= time.GetTimestamp();
        progress.Slot = 0;
        progress.Cursor = "0";
        progress.Found = 0;
        progress.Lengths = null;
        return new RetireVerdict(false, found, Complete: true, BlockedFor(progress));
    }

    // One unit of work, either a batch of lengths that skips a slot or a page of a scan; the budget is spent on its round trips, never on a skipped slot.
    private async Task<bool> StepAsync(string group, IReadOnlyList<int> slots, Progress progress, long floor, string retiring, CancellationToken cancellationToken)
    {
        var skipped = false;
        var trip = false;
        if (progress.Cursor == "0")
        {
            (skipped, trip) = await SkipsAsync(group, slots, progress, floor, cancellationToken).ConfigureAwait(false);
        }

        if (skipped)
        {
            progress.Slot++;
            return trip;
        }

        var reply = ((RedisResult[]?)await control.ExecuteAsync("HSCAN", [names.Control(group, slots[progress.Slot]), progress.Cursor, "MATCH", "sm:*", "COUNT", ScanPage], Master).ConfigureAwait(false))!;
        var values = ((RedisResult[]?)reply[1])!.Select(r => r.ToString()).ToArray();
        progress.Found += Enumerable.Range(0, values.Length / 2).Count(n => MarkerBinding.BindingOf(values[(2 * n) + 1]) == retiring);
        progress.Cursor = reply[0].ToString();
        if (progress.Cursor == "0")
        {
            progress.Slot++;
        }

        return true;
    }

    private TimeSpan BlockedFor(Progress progress) => progress.BlockedSince is { } since ? time.GetElapsedTime(since) : TimeSpan.Zero;

    // The lengths of the next slots come in one pipeline, so a slot with nothing to scan costs a share of one round trip.
    private async Task<(bool Skips, bool Fetched)> SkipsAsync(string group, IReadOnlyList<int> slots, Progress progress, long floor, CancellationToken cancellationToken)
    {
        var fetched = false;
        if (progress.Lengths is null || progress.Slot < progress.LengthsFrom || progress.Slot >= progress.LengthsFrom + progress.Lengths.Length)
        {
            fetched = true;
            var chunk = slots.Skip(progress.Slot).Take(LengthBatch).ToArray();
            progress.Lengths = await Task.WhenAll(chunk.Select(slot => control.HashLengthAsync(names.Control(group, slot), Master))).WaitAsync(cancellationToken).ConfigureAwait(false);
            progress.LengthsFrom = progress.Slot;
        }

        return (progress.Lengths[progress.Slot - progress.LengthsFrom] <= floor, fetched);
    }

    private sealed class Progress
    {
        public int Slot { get; set; }

        public string Cursor { get; set; } = "0";

        public int Found { get; set; }

        public long? BlockedSince { get; set; }

        public long[]? Lengths { get; set; }

        public int LengthsFrom { get; set; }
    }
}
