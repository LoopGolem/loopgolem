using System.Text.Json.Serialization;
using LoopGolem.Core.Protocol;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed record CodexContextReuseHint(
    [property: JsonPropertyName("recommended")] bool Recommended,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record CodexAgentOutcome(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("checks")] IReadOnlyList<string> Checks,
    [property: JsonPropertyName("blocker")] string Blocker,
    [property: JsonPropertyName("contextReuse")] CodexContextReuseHint ContextReuse);

public sealed class CodexCliService
{
    private const string StatusModel = "gpt-6-luna";
    private const string WindowsProbeCommand =
        "command -v codex; codex --version; codex login status";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly ProcessRunner _processRunner;
    private readonly WslRuntimeService _wsl;

    public CodexCliService(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
        _wsl = new WslRuntimeService(processRunner);
    }

    public Task<CodexRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        OperatingSystem.IsWindows()
            ? GetWindowsStatusAsync(cancellationToken)
            : GetNativeStatusAsync(cancellationToken);

    private async Task<CodexRuntimeStatus> GetWindowsStatusAsync(
        CancellationToken cancellationToken)
    {
        var distribution = await _wsl.ResolveDistributionAsync(
            cancellationToken);

        if (!distribution.Success ||
            string.IsNullOrWhiteSpace(distribution.Distribution))
        {
            return new CodexRuntimeStatus(
                Available: false,
                ChatGptAuthenticated: false,
                Version: null,
                Message: distribution.Error ??
                    "No user WSL distribution is available.",
                State: CodexRuntimeState.WslDistributionMissing,
                Runtime: "wsl",
                Distribution: null,
                Model: StatusModel);
        }

        var distro = distribution.Distribution;

        ProcessRunResult probe;
        try
        {
            probe = await _wsl.RunLoginShellCommandAsync(
                distro,
                WindowsProbeCommand,
                StatusTimeout,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                null,
                exception.Message,
                CodexRuntimeState.CodexCliMissing,
                "wsl",
                distro,
                StatusModel);
        }

        var probeText = JoinProcessText(probe);
        var chatGpt = IsChatGptLogin(probe, probeText);
        var version = ExtractVersion(probeText);

        if (probe.TimedOut || probe.ExitCode != 0)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                version,
                FormatDiagnostic("WSL Codex probe", probe),
                CodexRuntimeState.CodexCliMissing,
                "wsl",
                distro,
                StatusModel);
        }

        if (!chatGpt)
        {
            return new CodexRuntimeStatus(
                true,
                false,
                version,
                FormatDiagnostic("WSL Codex probe", probe),
                CodexRuntimeState.AuthenticationRequired,
                "wsl",
                distro,
                StatusModel);
        }

        return new CodexRuntimeStatus(
            true,
            true,
            version,
            $"Codex is ready inside WSL distribution '{distro}' using ChatGPT authentication.",
            CodexRuntimeState.Ready,
            "wsl",
            distro,
            StatusModel);
    }

    private async Task<CodexRuntimeStatus> GetNativeStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await _processRunner.RunAsync(
                "codex",
                ["--version"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            if (version.TimedOut || version.ExitCode != 0)
            {
                return new CodexRuntimeStatus(
                    false,
                    false,
                    null,
                    "Codex CLI could not be started.",
                    CodexRuntimeState.CodexCliMissing,
                    "native",
                    null,
                    StatusModel);
            }

            var login = await _processRunner.RunAsync(
                "codex",
                ["login", "status"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            var loginText = JoinProcessText(login);
            var chatGpt = IsChatGptLogin(login, loginText);

            var versionText = version.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(versionText))
            {
                versionText = version.StandardError.Trim();
            }

            return new CodexRuntimeStatus(
                Available: true,
                ChatGptAuthenticated: chatGpt,
                Version: string.IsNullOrWhiteSpace(versionText)
                    ? null
                    : versionText,
                Message: chatGpt
                    ? "Codex CLI is authenticated with ChatGPT."
                    : loginText.Trim(),
                State: chatGpt
                    ? CodexRuntimeState.Ready
                    : CodexRuntimeState.AuthenticationRequired,
                Runtime: "native",
                Distribution: null,
                Model: StatusModel);
        }
        catch (Exception exception)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                null,
                exception.Message,
                CodexRuntimeState.CodexCliMissing,
                "native",
                null,
                StatusModel);
        }
    }

    private static string JoinProcessText(ProcessRunResult run) =>
        string.Join(
            Environment.NewLine,
            new[] { run.StandardOutput, run.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsChatGptLogin(
        ProcessRunResult run,
        string text) =>
        run.ExitCode == 0 &&
        !run.TimedOut &&
        text.Contains(
            "Logged in using ChatGPT",
            StringComparison.OrdinalIgnoreCase);

    private static string? ExtractVersion(string text) =>
        text
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault(line =>
                line.Contains("codex", StringComparison.OrdinalIgnoreCase) &&
                line.Any(char.IsDigit));

    private static string FormatDiagnostic(
        string name,
        ProcessRunResult run) =>
        $"""
        {name}
        exit code: {run.ExitCode}
        timed out: {run.TimedOut}
        duration ms: {run.DurationMilliseconds}
        stdout:
        {run.StandardOutput.Trim()}
        stderr:
        {run.StandardError.Trim()}
        """.Trim();
}
