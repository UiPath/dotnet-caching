namespace UiPath.Caching.Migration;

/// <summary>The memory rules of the mover: the reason it must pause for memory now, or null.</summary>
internal interface IMoverMemory
{
    ValueTask<string?> PauseReasonAsync(CancellationToken cancellationToken);
}
