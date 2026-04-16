namespace Summerdawn.Kagami.Configuration;

public sealed class KagamiOptions
{
    /// <summary>Named endpoint definitions keyed by endpoint name.</summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = [];

    /// <summary>Named job definitions keyed by job key.</summary>
    public Dictionary<string, JobOptions> Jobs { get; set; } = [];

    /// <summary>Host-level execution settings.</summary>
    public KagamiHostOptions Host { get; set; } = new();
}
