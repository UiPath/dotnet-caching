namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FleetNode(string name)
{
    public string Name { get; } = name;

    public int? Fl { get; set; } = 5;

    public bool Pb { get; set; } = true;

    public bool Pin { get; set; } = true;

    public bool Pbg { get; set; } = true;

    public string? Ca { get; set; } = "a";

    public bool Acks { get; set; } = true;

    public int Pi { get; set; }

    public bool Byp { get; set; }

    public bool Recycles { get; set; }

    public TimeSpan Age { get; set; }
}
