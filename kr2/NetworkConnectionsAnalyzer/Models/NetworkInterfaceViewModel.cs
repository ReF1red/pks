namespace NetworkConnectionsAnalyzer.Models;

public sealed class NetworkInterfaceViewModel
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string IpAddress { get; init; }
    public required string SubnetMask { get; init; }
    public required string MacAddress { get; init; }
    public required string Status { get; init; }
    public required string SpeedMbps { get; init; }
    public required string InterfaceType { get; init; }

    public string DisplayName => $"{Name} ({InterfaceType})";
}
