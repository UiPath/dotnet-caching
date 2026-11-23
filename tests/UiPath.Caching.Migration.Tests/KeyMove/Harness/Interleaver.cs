namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

// Explores the schedules of a scenario: every interleaving up to a cap by depth-first replay, or seeded random schedules for a scenario too large to enumerate.
internal static class Interleaver
{
    public static async Task<(int Runs, List<string> Violations)> ExploreAsync(Func<Func<int, int, int>, Task<InterleavingRun>> run, int maxRuns, bool random)
    {
        var violations = new List<string>();
        var runs = 0;
        if (random)
        {
            for (var seed = 0; seed < maxRuns; seed++)
            {
                var rng = new Random(seed);
                var result = await run((_, options) => rng.Next(options));
                runs++;
                Collect(violations, result, $"seed {seed}");
            }

            return (runs, violations);
        }

        var frontier = new Stack<int[]>();
        frontier.Push([]);
        while (frontier.Count > 0 && runs < maxRuns)
        {
            var prefix = frontier.Pop();
            var result = await run((index, _) => index < prefix.Length ? prefix[index] : 0);
            runs++;
            Collect(violations, result, "prefix " + string.Join(',', prefix));
            for (var i = prefix.Length; i < result.Trace.Count; i++)
            {
                for (var alternative = result.Trace[i].Chosen + 1; alternative < result.Trace[i].Options; alternative++)
                {
                    frontier.Push([.. result.Trace.Take(i).Select(t => t.Chosen), alternative]);
                }
            }
        }

        return (runs, violations);
    }

    private static void Collect(List<string> violations, InterleavingRun result, string label)
    {
        foreach (var violation in result.Violations)
        {
            violations.Add($"{label} (schedule {string.Join(',', result.Trace.Select(t => t.Chosen))}): {violation}");
        }
    }
}
