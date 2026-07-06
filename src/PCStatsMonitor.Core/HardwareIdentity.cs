namespace PCStatsMonitor.Core;

/// <summary>Immutable identity record for a hardware component, resolved once at startup.</summary>
public sealed record HardwareIdentity(string Domain, string Vendor, string Model)
{
    public static readonly HardwareIdentity Unknown = new("Unknown", "Unknown", "Unknown");

    public override string ToString() => $"{Vendor} {Model}";
}
