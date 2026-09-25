using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed class EnvironmentCapabilityService(
    ProcessRunner processRunner,
    IMissionStore store)
{
    private sealed record ToolProbe(
        string Name,
        string Executable,
        IReadOnlyList<string> Arguments);

    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyList<ToolProbe> ToolProbes =
    [
        new("git", "git", ["--version"]),
        new("dotnet", "dotnet", ["--version"])
    ];

    private readonly WslRuntimeService _wsl =
        new(processRunner);

    public async Task<MissionCapabilitySnapshot>
        GetOrCaptureAsync(
            Mission mission,
            CodexRuntimeStatus codexStatus,
            CancellationToken cancellationToken = default)
    {
        var persisted =
            await store.GetCapabilitySnapshotAsync(
                mission.Id,
                cancellationToken);

        if (persisted is not null)
        {
            return persisted;
        }

        var snapshot =
            await CaptureAsync(
                mission,
                codexStatus,
                cancellationToken);

        await store.UpsertCapabilitySnapshotAsync(
            snapshot,
            cancellationToken);

        return snapshot;
    }

    private async Task<MissionCapabilitySnapshot>
        CaptureAsync(
            Mission mission,
            CodexRuntimeStatus codexStatus,
            CancellationToken cancellationToken)
    {
        var hostTask =
            ProbeHostEnvironmentAsync(
                mission.WorkspacePath,
                cancellationToken);
        var agentTask =
            ProbeAgentEnvironmentAsync(
                mission.WorkspacePath,
                codexStatus,
                cancellationToken);

        await Task.WhenAll(
            hostTask,
            agentTask);

        return new MissionCapabilitySnapshot(
            mission.Id,
            await agentTask,
            await hostTask,
            DateTimeOffset.UtcNow);
    }

    private async Task<ExecutionEnvironmentCapabilities>
        ProbeHostEnvironmentAsync(
            string workspace,
            CancellationToken cancellationToken)
    {
        var tools =
            await ProbeToolsAsync(
                probe =>
                    ProbeNativeToolAsync(
                        probe,
                        workspace,
                        cancellationToken));

        return new ExecutionEnvironmentCapabilities(
            Runtime: "host",
            OperatingSystem: GetHostOperatingSystem(),
            Distribution: null,
            Tools: tools);
    }

    private async Task<ExecutionEnvironmentCapabilities>
        ProbeAgentEnvironmentAsync(
            string workspace,
            CodexRuntimeStatus codexStatus,
            CancellationToken cancellationToken)
    {
        if (string.Equals(
                codexStatus.Runtime,
                "wsl",
                StringComparison.OrdinalIgnoreCase))
        {
            var distribution =
                codexStatus.Distribution
                ?? throw new InvalidOperationException(
                    "Codex reported WSL runtime without a distribution.");

            var tools =
                await ProbeToolsAsync(
                    probe =>
                        ProbeWslToolAsync(
                            distribution,
                            probe,
                            cancellationToken));

            return new ExecutionEnvironmentCapabilities(
                Runtime: "wsl",
                OperatingSystem: "linux",
                Distribution: distribution,
                Tools: tools);
        }

        var nativeTools =
            await ProbeToolsAsync(
                probe =>
                    ProbeNativeToolAsync(
                        probe,
                        workspace,
                        cancellationToken));

        return new ExecutionEnvironmentCapabilities(
            Runtime: codexStatus.Runtime,
            OperatingSystem: GetHostOperatingSystem(),
            Distribution: codexStatus.Distribution,
            Tools: nativeTools);
    }

    private static async Task<IReadOnlyList<ToolCapability>>
        ProbeToolsAsync(
            Func<ToolProbe, Task<ToolCapability>> probe)
    {
        var tasks =
            ToolProbes
                .Select(probe)
                .ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<ToolCapability>
        ProbeNativeToolAsync(
            ToolProbe probe,
            string workingDirectory,
            CancellationToken cancellationToken)
    {
        try
        {
            var run =
                await processRunner.RunAsync(
                    probe.Executable,
                    probe.Arguments,
                    workingDirectory,
                    ProbeTimeout,
                    cancellationToken);

            return FromProcessResult(
                probe.Name,
                run);
        }
        catch (Exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolCapability(
                probe.Name,
                false,
                null);
        }
    }

    private async Task<ToolCapability>
        ProbeWslToolAsync(
            string distribution,
            ToolProbe probe,
            CancellationToken cancellationToken)
    {
        try
        {
            var run =
                await _wsl.RunLoginShellExecutableAsync(
                    distribution,
                    probe.Executable,
                    probe.Arguments,
                    ProbeTimeout,
                    cancellationToken);

            return FromProcessResult(
                probe.Name,
                run);
        }
        catch (Exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolCapability(
                probe.Name,
                false,
                null);
        }
    }

    private static ToolCapability FromProcessResult(
        string name,
        ProcessRunResult run)
    {
        if (run.TimedOut ||
            run.ExitCode != 0)
        {
            return new ToolCapability(
                name,
                false,
                null);
        }

        var version =
            FirstNonEmptyLine(
                run.StandardOutput) ??
            FirstNonEmptyLine(
                run.StandardError);

        return new ToolCapability(
            name,
            true,
            version);
    }

    private static string? FirstNonEmptyLine(
        string value) =>
        value
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string GetHostOperatingSystem() =>
        OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";

    internal static string FormatForPrompt(
        MissionCapabilitySnapshot snapshot)
    {
        static string FormatEnvironment(
            string label,
            ExecutionEnvironmentCapabilities environment)
        {
            var distribution =
                string.IsNullOrWhiteSpace(
                    environment.Distribution)
                    ? string.Empty
                    : $", distribution={environment.Distribution}";

            var tools =
                string.Join(
                    Environment.NewLine,
                    environment.Tools.Select(
                        tool =>
                            $"- {tool.Name}: " +
                            (tool.Available
                                ? $"available ({tool.Version ?? "version unknown"})"
                                : "UNAVAILABLE")));

            return $"""
                {label}: runtime={environment.Runtime}, os={environment.OperatingSystem}{distribution}
                Probed tools:
                {tools}
                """;
        }

        return $"""
            {FormatEnvironment(
                "AGENT ENVIRONMENT (where Codex/Luna commands run)",
                snapshot.AgentEnvironment)}

            {FormatEnvironment(
                "DETERMINISTIC HOST ENVIRONMENT (where LoopGolem run_command/build checks run)",
                snapshot.HostEnvironment)}

            Only the tools listed above were probed. An unlisted tool has UNKNOWN availability; do not treat it as unavailable.
            """;
    }
}
