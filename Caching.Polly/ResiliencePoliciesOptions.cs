namespace UiPath.Platform.Caching.Polly;

public class ResiliencePoliciesOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan DurationOfBreak { get; set; } = TimeSpan.FromMinutes(1);

    public int ExceptionsAllowedBeforeBreaking { get; set; } = 500;

    public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public int? RetryCount { get; set; } = 2;
}
