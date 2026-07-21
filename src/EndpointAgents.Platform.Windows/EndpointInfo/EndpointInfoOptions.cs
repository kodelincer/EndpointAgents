namespace EndpointAgents.Platform.Windows.EndpointInfo;

/// <summary>Configuration for the small connectivity probe included in endpoint information.</summary>
public sealed class EndpointInfoOptions
{
    public string InternetCheckUrl { get; set; } =
        "http://www.msftconnecttest.com/connecttest.txt";

    public string ExpectedInternetContent { get; set; } = "Microsoft Connect Test";

    public int InternetCheckTimeoutSeconds { get; set; } = 8;
}
