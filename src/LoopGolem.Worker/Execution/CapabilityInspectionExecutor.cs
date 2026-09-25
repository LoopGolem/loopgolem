using System.Runtime.InteropServices;
using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Execution;

public sealed class CapabilityInspectionExecutor(
    ProcessRunner processRunner,
    CodexCliService codex) : IMissionTaskExecutor
{
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(15);

    private readonly WslRuntimeService _wsl = new(processRunner);

    public MissionTaskKind Kind =>
        MissionTaskKind.InspectCapabilities;

    public async Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var host = new ExecutionEnvironmentCapabilities(
            ExecutionEnvironmentKind.Host,
            $"native/{RuntimeInformation.ProcessArchitecture}",
            RuntimeInformation.OSDescription,
            null,
            [
                await ProbeHostToolAsync(
                    "git",
                    ["--version"],
                    mission.WorkspacePath,
                    cancellationToken),
                await ProbeHostToolAsync(
                    "dotnet",
                    ["--version"],
                    mission.WorkspacePath,
                    cancellationToken)
            ]);

        var codexStatus = await codex.GetStatusAsync(
            cancellationToken);

        ExecutionEnvironmentCapabilities agent;

        if (OperatingSystem.IsWindows())
        {
            var distribution = codexStatus.Distribution;

            IReadOnlyList<ToolCapability> tools =
                string.IsNullOrWhiteSpace(distribution)
                    ? [
                        Unavailable(
                            "git",
                            "No user WSL distribution is available."),
                        Unavailable(
                            "dotnet",
                            "No user WSL distribution is available."),
                        CodexCapability(codexStatus)
                    ]
                    : [
                        await ProbeWslToolAsync(
                            distribution,
                            "git",
                            ["--version"],
                            cancellationToken),
                        await ProbeWslToolAsync(
                            distribution,
                            "dotnet",
                            ["--version"],
                            cancellationToken),
                        CodexCapability(codexStatus)
                    ];

            agent = new ExecutionEnvironmentCapabilities(
                ExecutionEnvironmentKind.Agent,
                "wsl2",
                "Linux (WSL2)",
                distribution,
                tools);
        }
        else
        {
            agent = new ExecutionEnvironmentCapabilities(
                ExecutionEnvironmentKind.Agent,
                $"native/{RuntimeInformation.ProcessArchitecture}",
                RuntimeInformation.OSDescription,
                null,
                [
                    await ProbeHostToolAsync(
                        "git",
                        ["--version"],
                        mission.WorkspacePath,
                        cancellationToken),
                    await ProbeHostToolAsync(
                        "dotnet",
                        ["--version"],
                        mission.WorkspacePath,
                        cancellationToken),
                    CodexCapability(codexStatus)
                ]);
        }

        var snapshot = new MissionCapabilitySnapshot(
            agent,
            host,
            DateTimeOffset.UtcNow);

        var agentDotNet = snapshot.FindAgentTool("dotnet");
        var hostDotNet = snapshot.FindHostTool("dotnet");

        return TaskExecutionResult.Succeeded(
            $"Capability inspection complete: agent dotnet={FormatAvailability(agentDotNet)}, host dotnet={FormatAvailability(hostDotNet)}.",
            JsonSerializer.Serialize(snapshot));
    }

    private async Task<ToolCapability> ProbeHostToolAsync(
        string name,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await processRunner.RunAsync(
                name,
                arguments,
                workingDirectory,
                ProbeTimeout,
                cancellationToken);

            return ToCapability(name, run);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return Unavailable(name, exception.Message);
        }
    }

    private async Task<ToolCapability> ProbeWslToolAsync(
        string distribution,
        string name,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await _wsl.RunLoginShellExecutableAsync(
                distribution,
                name,
                arguments,
                ProbeTimeout,
                cancellationToken);

            return ToCapability(name, run);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return Unavailable(name, exception.Message);
        }
    }

    private static ToolCapability ToCapability(
        string name,
        ProcessRunResult run)
    {
        if (run.TimedOut || run.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(run.StandardError)
                ? run.StandardOutput.Trim()
                : run.StandardError.Trim();

            return Unavailable(
                name,
                string.IsNullOrWhiteSpace(details)
                    ? $"Probe exited with code {run.ExitCode}."
                    : details);
        }

        var version = FirstLine(run.StandardOutput);
        if (string.IsNullOrWhiteSpace(version))
        {
            version = FirstLine(run.StandardError);
        }

        return new ToolCapability(
            name,
            true,
            string.IsNullOrWhiteSpace(version)
                ? null
                : version,
            null);
    }

    private static ToolCapability CodexCapability(
        LoopGolem.Core.Protocol.CodexRuntimeStatus status) =>
        new(
            "codex",
            status.Available && status.ChatGptAuthenticated,
            status.Version,
            status.Message);

    private static ToolCapability Unavailable(
        string name,
        string details) =>
        new(
            name,
            false,
            null,
            details);

    private static string? FirstLine(string value) =>
        value
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string FormatAvailability(
        ToolCapability? capability) =>
        capability is { Available: true }
            ? "available"
            : "unavailable";
}
