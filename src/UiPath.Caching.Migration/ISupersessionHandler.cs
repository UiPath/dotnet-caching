namespace UiPath.Caching.Migration;

/// <summary>The seam where the mover hands a key whose capture met a live supersession marker to the code that owns the marker; the marker itself is not part of the mover.</summary>
internal interface ISupersessionHandler
{
    Task<SupersessionAction> OnSupersededAsync(SupersededKey context, CancellationToken cancellationToken);
}
