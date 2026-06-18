namespace UiPath.Platform.Sample.AspNetCore.Models;

public class SampleResource
{
    public string? Name { get; set; }

    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}
