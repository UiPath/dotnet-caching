namespace UiPath.Caching.Migration;

/// <summary>What makes the mover's run of the promotion steps differ from a read-through's: the minimum remaining life, the supersession seam, and the duplicate rule.</summary>
/// <param name="MinTtl">A key whose remaining life is a count below this is left alone; zero moves every key. Never set for a <c>NoOrigin</c> cache.</param>
/// <param name="Supersession">Told when the capture meets a live supersession marker.</param>
/// <param name="ObserveSourceTtl">Receives the source TTL and its request time in Unix milliseconds.</param>
internal sealed record MoverFlavor(TimeSpan MinTtl, ISupersessionHandler Supersession, Action<long, long>? ObserveSourceTtl = null);
