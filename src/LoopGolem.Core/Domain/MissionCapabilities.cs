namespace LoopGolem.Core.Domain;

public enum ExecutionEnvironmentKind
{
    Agent,
    Host
}

public sealed record ToolCapability(
    string Name,
    bool Available,
    string? Version,
    string? Details);

public sealed record ExecutionEnvironmentCapabilities(
    ExecutionEnvironmentKind Kind,
    string Runtime,
    string OperatingSystem,
    string? Distribution,
    IReadOnlyList<ToolCapability> Tools);

public sealed record MissionCapabilitySnapshot(
    ExecutionEnvironmentCapabilities AgentEnvironment,
    ExecutionEnvironmentCapabilities HostEnvironment,
    DateTimeOffset CapturedAtUtc)
{
    public ToolCapability? FindAgentTool(string name) =>
        AgentEnvironment.Tools.FirstOrDefault(
            tool => string.Equals(
                tool.Name,
                name,
                StringComparison.OrdinalIgnoreCase));

    public ToolCapability? FindHostTool(string name) =>
        HostEnvironment.Tools.FirstOrDefault(
            tool => string.Equals(
                tool.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
}
