namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Conflict resolution policy for a container.
/// </summary>
public class ConflictResolutionPolicy
{
    public ConflictResolutionMode Mode { get; set; } = ConflictResolutionMode.LastWriterWins;

    public string ConflictResolutionPath { get; set; } = "/_ts";

    public string? ConflictResolutionProcedure { get; set; }
}

public enum ConflictResolutionMode
{
    LastWriterWins,
    Custom
}
