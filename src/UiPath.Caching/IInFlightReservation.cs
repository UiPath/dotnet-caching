namespace UiPath.Caching;

/// <summary>A key taken by <see cref="InFlight{TKey, TResult}.TryReserve"/>; ending it takes the key out of the table before any joined caller sees the outcome.</summary>
internal interface IInFlightReservation<TResult>
{
    void Complete(TResult result);

    /// <summary>Joined callers share <paramref name="failure"/>; <see cref="InFlightAbandonedException"/> tells them the run was given up rather than failed.</summary>
    void Fail(Exception failure);
}
