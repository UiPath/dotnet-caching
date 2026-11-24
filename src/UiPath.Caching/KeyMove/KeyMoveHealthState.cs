namespace UiPath.Caching.KeyMove;

/// <summary>How much load the key-move mover may put on Redis, as an <see cref="IKeyMoveHealthSource"/> judges it.</summary>
internal enum KeyMoveHealthState
{
    /// <summary>No verdict; reads as red, so a default value never lets the mover run.</summary>
    Unknown = 0,

    /// <summary>Redis has room: the mover may raise its rate.</summary>
    Green = 1,

    /// <summary>Redis is under some strain: the mover halves its rate.</summary>
    Amber = 2,

    /// <summary>Redis is overloaded: the mover pauses until the state improves.</summary>
    Red = 3,
}
