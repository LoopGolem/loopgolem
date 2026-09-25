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
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = false;

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        stopwatch.Stop();

        return new ProcessRunResult(
            fileName,
            argumentList,
            process.HasExited ? process.ExitCode : -1,
            timedOut,
            stopwatch.ElapsedMilliseconds,
            stdout,
            stderr);
    }


    public async Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Func<string, Task> onStandardOutputLine,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(onStandardOutputLine);

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

        var stdoutTask = PumpStandardOutputAsync(
            process.StandardOutput,
            onStandardOutputLine);
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = false;

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var (stdout, callbackError) = await stdoutTask;
        var stderr = await stderrTask;

        stopwatch.Stop();

        if (callbackError is not null)
        {
            throw new InvalidOperationException(
                "A streaming process-output observer failed.",
                callbackError);
        }

        return new ProcessRunResult(
            fileName,
            argumentList,
            process.HasExited ? process.ExitCode : -1,
            timedOut,
            stopwatch.ElapsedMilliseconds,
            stdout,
            stderr);
    }

    private static async Task<(string Output, Exception? CallbackError)>
        PumpStandardOutputAsync(
            StreamReader reader,
            Func<string, Task> onLine)
    {
        var output = new StringBuilder();
        Exception? callbackError = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);

            if (callbackError is not null)
            {
                continue;
            }

            try
            {
                await onLine(line);
            }
            catch (Exception exception)
            {
                // Keep draining stdout so the child process cannot block on a
                // full pipe. Surface the observer failure after the process exits.
                callbackError = exception;
            }
        }

        return (output.ToString(), callbackError);
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
