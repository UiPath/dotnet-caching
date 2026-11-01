namespace UiPath.Caching;

/// <summary>A run its work ends: before the work's task completes, the work calls <see cref="Complete"/> or <see cref="Fail"/> once, so the run is out of the table and settled for the callers that joined it before the caller that started it resumes on that task.</summary>
internal interface IInFlightEnd<in TResult> : IInFlightRun
{
    void Complete(TResult result);

    /// <summary>An <see cref="OperationCanceledException"/> once the run's own token is cancelled ends it as cancelled; any other failure is shared with the callers that joined.</summary>
    void Fail(Exception failure);
}
