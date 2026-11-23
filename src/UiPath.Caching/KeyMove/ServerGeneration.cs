namespace UiPath.Caching.KeyMove;

internal readonly record struct ServerGeneration(string RunId, string ReplicationId)
{
    public bool Includes(string runId, string replicationId)
    {
        var runs = RunId.Split(',');
        var replications = ReplicationId.Split(',');
        return runs.Length == replications.Length && Enumerable.Range(0, runs.Length).Any(i => runs[i] == runId && replications[i] == replicationId);
    }
}
