namespace LoopGolem.Core.Domain;

public sealed record ToolCapability(
    string Name,
    bool Available,
    string? Version);

public sealed record ExecutionEnvironmentCapabilities(
    string Runtime,
    string OperatingSystem,
    string? Distribution,
    IReadOnlyList<ToolCapability> Tools);

public sealed record MissionCapabilitySnapshot(
    string MissionId,
    ExecutionEnvironmentCapabilities AgentEnvironment,
    ExecutionEnvironmentCapabilities HostEnvironment,
    DateTimeOffset CapturedAtUtc);
