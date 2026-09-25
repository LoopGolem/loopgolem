using System.Diagnostics;
using System.Text;

namespace LoopGolem.Worker.Infrastructure;

public sealed record ProcessRunResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    bool TimedOut,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError);

public sealed class ProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null) =>
        RunStreamingAsync(
            fileName,
            arguments,
            workingDirectory,
            timeout,
            cancellationToken,
            standardInput,
            environmentVariables,
            null);

    public async Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, Task>? standardOutputLineHandler = null)
    {
        var argumentList = arguments.ToArray();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        if (standardInput is not null)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start process '{fileName}'.");
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = ReadLinesAsync(
            process.StandardOutput,
            stdout,
            standardOutputLineHandler);
        var stderrTask = ReadLinesAsync(
            process.StandardError,
            stderr,
            null);

        var timedOut = false;

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var waitTask = process.WaitForExitAsync(timeoutSource.Token);

        try
        {
            var first = await Task.WhenAny(waitTask, stdoutTask);
            if (first == stdoutTask && stdoutTask.IsFaulted)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await stdoutTask;
            }

            await waitTask;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(
                CancellationToken.None);

            try
            {
                await Task.WhenAll(
                    stdoutTask,
                    stderrTask);
            }
            catch
            {
                // Preserve the caller cancellation as the primary outcome.
                // Any thread.started persistence callback already ran with
                // its own crash-safe persistence semantics.
            }

            throw;
        }
        catch
        {
            TryKillProcessTree(process);
            throw;
        }

        await stdoutTask;
        await stderrTask;

        stopwatch.Stop();

        return new ProcessRunResult(
            fileName,
            argumentList,
            process.HasExited ? process.ExitCode : -1,
            timedOut,
            stopwatch.ElapsedMilliseconds,
            stdout.ToString(),
            stderr.ToString());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder destination,
        Func<string, Task>? lineHandler)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            destination.AppendLine(line);

            if (lineHandler is not null)
            {
                await lineHandler(line);
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
