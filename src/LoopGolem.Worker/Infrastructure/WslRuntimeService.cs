namespace LoopGolem.Worker.Infrastructure;

public sealed record WslDistributionResolution(
    bool Success,
    string? Distribution,
    string? Error);

public sealed class WslRuntimeService(ProcessRunner processRunner)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<WslDistributionResolution> ResolveDistributionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WslDistributionResolution(
                false,
                null,
                "WSL is only used by LoopGolem on Windows.");
        }

        var configured = Environment.GetEnvironmentVariable(
            "LOOPGOLEM_WSL_DISTRO");

        ProcessRunResult list;
        try
        {
            list = await processRunner.RunAsync(
                "wsl.exe",
                ["--list", "--quiet"],
                Environment.CurrentDirectory,
                ProbeTimeout,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new WslDistributionResolution(
                false,
                null,
                $"WSL could not be started: {exception.Message}");
        }

        if (list.TimedOut || list.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(list.StandardError)
                ? list.StandardOutput
                : list.StandardError;

            return new WslDistributionResolution(
                false,
                null,
                string.IsNullOrWhiteSpace(error)
                    ? "WSL is unavailable."
                    : error.Trim());
        }

        var distributions = ParseDistributionList(list.StandardOutput);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredMatch = distributions.FirstOrDefault(
                name => string.Equals(
                    name,
                    configured,
                    StringComparison.OrdinalIgnoreCase));

            if (configuredMatch is null)
            {
                return new WslDistributionResolution(
                    false,
                    null,
                    $"Configured WSL distribution '{configured}' was not found.");
            }

            if (IsInfrastructureDistribution(configuredMatch))
            {
                return new WslDistributionResolution(
                    false,
                    null,
                    $"Configured WSL distribution '{configured}' is reserved for infrastructure and cannot host LoopGolem Codex work.");
            }

            return new WslDistributionResolution(
                true,
                configuredMatch,
                null);
        }

        var selected = distributions.FirstOrDefault(
            name => !IsInfrastructureDistribution(name));

        return selected is null
            ? new WslDistributionResolution(
                false,
                null,
                "No user Linux distribution is installed in WSL. Install one, for example Ubuntu.")
            : new WslDistributionResolution(
                true,
                selected,
                null);
    }

    public Task<ProcessRunResult> RunAsync(
        string distribution,
        IEnumerable<string> linuxArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        var arguments = new List<string>
        {
            "--distribution",
            distribution,
            "--"
        };

        arguments.AddRange(linuxArguments);

        return processRunner.RunAsync(
            "wsl.exe",
            arguments,
            Environment.CurrentDirectory,
            timeout,
            cancellationToken,
            standardInput);
    }

    public async Task<string> ConvertWindowsPathAsync(
        string distribution,
        string windowsPath,
        CancellationToken cancellationToken = default)
    {
        var run = await RunAsync(
            distribution,
            ["wslpath", "-a", "-u", windowsPath],
            ProbeTimeout,
            cancellationToken);

        if (run.TimedOut || run.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(run.StandardError)
                ? run.StandardOutput
                : run.StandardError;

            throw new InvalidOperationException(
                $"Could not translate Windows path '{windowsPath}' into WSL: {error.Trim()}");
        }

        var path = run.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"WSL returned an empty path for '{windowsPath}'.");
        }

        return path;
    }

    internal static IReadOnlyList<string> ParseDistributionList(
        string output) =>
        output
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsInfrastructureDistribution(string name) =>
        name.StartsWith(
            "docker-desktop",
            StringComparison.OrdinalIgnoreCase);
}
