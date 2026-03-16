namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Captures emulator lifecycle timestamps that are reused across responses.
/// </summary>
public sealed class EmulatorRuntimeState
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
}
