using System.Security.Cryptography;

namespace LoopGolem.Worker.Infrastructure;

public sealed record GitWorkspaceSnapshot(
    IReadOnlyDictionary<string, string> Files)
{
    public static async Task<GitWorkspaceSnapshot> CaptureAsync(
        ProcessRunner processRunner,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        var tracked = await processRunner.RunAsync(
            "git",
            ["diff", "--name-only", "HEAD", "--"],
            workspace,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        var untracked = await processRunner.RunAsync(
            "git",
            ["ls-files", "--others", "--exclude-standard"],
            workspace,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (tracked.ExitCode != 0 || untracked.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not capture Git workspace state.");
        }

        var paths = tracked.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(untracked.StandardOutput.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Normalize)
            .Distinct(PathComparer)
            .ToArray();

        var files = new Dictionary<string, string>(PathComparer);
        foreach (var path in paths)
        {
            files[path] = await HashPathAsync(workspace, path, cancellationToken);
        }

        return new GitWorkspaceSnapshot(files);
    }

    public IReadOnlyList<string> ChangesSince(GitWorkspaceSnapshot before)
    {
        return Files.Keys
            .Concat(before.Files.Keys)
            .Distinct(PathComparer)
            .Where(path =>
                !Files.TryGetValue(path, out var afterHash) ||
                !before.Files.TryGetValue(path, out var beforeHash) ||
                !string.Equals(afterHash, beforeHash, StringComparison.Ordinal))
            .OrderBy(path => path, PathComparer)
            .ToArray();
    }

    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static async Task<string> HashPathAsync(
        string workspace,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(
            workspace,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
        {
            return "<missing>";
        }

        await using var stream = File.OpenRead(fullPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
