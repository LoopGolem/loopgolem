namespace LoopGolem.Worker.Infrastructure;

public sealed class GitSnapshotService(ProcessRunner processRunner)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> GetHeadCommitAsync(
        string workspace,
        CancellationToken cancellationToken = default)
    {
        var run = await RunGitAsync(
            workspace,
            ["rev-parse", "HEAD"],
            cancellationToken);

        if (run.TimedOut || run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not resolve workspace HEAD: {GetError(run)}");
        }

        var commit = run.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new InvalidOperationException(
                "Git returned an empty HEAD commit.");
        }

        return commit;
    }

    public async Task<string> CreateSnapshotCommitAsync(
        string workspace,
        string baseCommit,
        string missionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(missionId);

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "LoopGolem",
            "git-snapshots",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRoot);

        var tempIndex = Path.Combine(tempRoot, "index");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = tempIndex,
            ["GIT_AUTHOR_NAME"] = "LoopGolem",
            ["GIT_AUTHOR_EMAIL"] = "loopgolem@local.invalid",
            ["GIT_COMMITTER_NAME"] = "LoopGolem",
            ["GIT_COMMITTER_EMAIL"] = "loopgolem@local.invalid"
        };

        try
        {
            await RequireSuccessAsync(
                workspace,
                ["read-tree", baseCommit],
                environment,
                cancellationToken,
                "initialize temporary Git index");

            await RequireSuccessAsync(
                workspace,
                ["add", "-A", "--", "."],
                environment,
                cancellationToken,
                "stage snapshot into temporary Git index");

            var treeRun = await RequireSuccessAsync(
                workspace,
                ["write-tree"],
                environment,
                cancellationToken,
                "write snapshot tree");

            var tree = treeRun.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(tree))
            {
                throw new InvalidOperationException(
                    "Git returned an empty snapshot tree.");
            }

            var commitRun = await RequireSuccessAsync(
                workspace,
                [
                    "commit-tree",
                    tree,
                    "-p",
                    baseCommit,
                    "-m",
                    $"LoopGolem validation snapshot {missionId}"
                ],
                environment,
                cancellationToken,
                "create unreachable validation snapshot commit");

            var commit = commitRun.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(commit))
            {
                throw new InvalidOperationException(
                    "Git returned an empty snapshot commit.");
            }

            return commit;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Snapshot commit already lives in the repository object store.
            }
        }
    }

    private async Task<ProcessRunResult> RequireSuccessAsync(
        string workspace,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        string operation)
    {
        var run = await processRunner.RunAsync(
            "git",
            arguments,
            workspace,
            GitTimeout,
            cancellationToken,
            environmentVariables: environment);

        if (run.TimedOut || run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {GetError(run)}");
        }

        return run;
    }

    private Task<ProcessRunResult> RunGitAsync(
        string workspace,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(
            "git",
            arguments,
            workspace,
            GitTimeout,
            cancellationToken);

    private static string GetError(ProcessRunResult run)
    {
        var error = string.IsNullOrWhiteSpace(run.StandardError)
            ? run.StandardOutput
            : run.StandardError;

        error = error.Trim();

        return string.IsNullOrWhiteSpace(error)
            ? $"git exited with code {run.ExitCode}."
            : error;
    }
}
