namespace UiPath.Caching.Tests.PhaseB.Harness;

// Visits every ordering of the actors' steps, and every alternative of a step, depth first with a visited set of world fingerprints.
// Two orderings that reach the same world are one state (their futures are the same), so the search is exhaustive over orderings without running each one.
internal sealed class Explorer(long maxStates)
{
    private readonly Dictionary<(ulong, ulong), double> _memo = [];
    private readonly List<(int Actor, int Choice)> _path = [];
    private World _initial = null!;
    private long _terminals;
    private long _blocked;
    private bool _truncated;
    private Violation? _violation;
    private List<string> _schedule = [];

    public ExploreResult Run(World initial)
    {
        _initial = initial;
        var schedules = Visit(initial);
        return new ExploreResult(_memo.Count, schedules, _terminals, _blocked, _truncated, _violation, _schedule);
    }

    // A read that starts when everything has finished sees the state the last completed mutations left.
    private static Violation? Final(World w)
    {
        for (var key = 0; key < w.Config.Keys.Length; key++)
        {
            var read = w.Clone();
            read.Nodes[0] = read.Auth;
            read.Actors = [.. read.Actors, new ReadActor("final", 0, key)];
            var last = read.Actors.Length - 1;
            for (var i = 0; i < 200 && read.Enabled(last); i++)
            {
                read.Step(last, 0);
            }

            if (read.Violation is { } v)
            {
                return v with { Final = true };
            }
        }

        return null;
    }

    private double Visit(World w)
    {
        if (w.Violation is { } broken)
        {
            Found(broken);
            return 0;
        }

        var key = w.Fingerprint();
        if (_memo.TryGetValue(key, out var known))
        {
            return known;
        }

        if (_memo.Count >= maxStates)
        {
            _truncated = true;
            return 0;
        }

        _memo[key] = 0;
        var paths = 0d;
        var any = false;
        for (var i = 0; i < w.Actors.Length && _violation is null; i++)
        {
            var choices = w.Actors[i].Done ? 0 : w.Actors[i].Choices(w);
            for (var c = 0; c < choices && _violation is null; c++)
            {
                any = true;
                var child = w.Clone();
                child.Step(i, c);
                _path.Add((i, c));
                paths += Visit(child);
                _path.RemoveAt(_path.Count - 1);
            }
        }

        if (!any)
        {
            _terminals++;
            if (w.Actors.Any(a => !a.Done))
            {
                _blocked++;
            }

            paths = 1;
            if (Final(w) is { } violation)
            {
                Found(violation);
            }
        }

        _memo[key] = paths;
        return paths;
    }

    private void Found(Violation violation)
    {
        if (_violation is not null)
        {
            return;
        }

        _violation = violation;
        var replay = _initial.Clone();
        replay.Trace = [];
        foreach (var (actor, choice) in _path)
        {
            replay.Step(actor, choice);
        }

        if (violation.Final)
        {
            replay.Trace.Add("-- all actors have finished; a final read starts --");
            Final(replay);
        }

        _schedule = replay.Trace;
    }
}
