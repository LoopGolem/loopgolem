using System.IO.Pipes;
using System.Text.Json;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Ipc;

public sealed class WorkerPipeServer(
    IMissionStore store,
    IMissionOrchestrator orchestrator,
    LoopGolem.Worker.Agents.CodexCliService codex)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                WorkerProtocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true)
        {
            AutoFlush = true
        };

        WorkerResponse response;

        try
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                response = Error(
                    WorkerErrorCodes.EmptyRequest,
                    "Empty request.");
            }
            else
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions);
                response = request is null
                    ? Error(WorkerErrorCodes.InvalidRequest, "Invalid request.")
                    : await HandleRequestAsync(request, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            response = Error(
                WorkerErrorCodes.InvalidJson,
                $"Invalid JSON: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = Error(
                WorkerErrorCodes.InternalError,
                exception.Message);
        }

        try
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response, JsonOptions));
        }
        catch (IOException)
        {
            // The client may time out, close the window, or otherwise disconnect
            // before a slower request finishes. A broken response pipe is a normal
            // transport condition and must not stop the Worker.
        }
        catch (ObjectDisposedException)
        {
            // The connection was disposed before the response could be written.
        }
    }

    private async Task<WorkerResponse> HandleRequestAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case WorkerProtocol.Ping:
                return new WorkerResponse(true, WorkerStatus: "ready");

            case WorkerProtocol.GetCodexStatus:
                return new WorkerResponse(
                    true,
                    CodexStatus: await codex.GetStatusAsync(cancellationToken));

            case WorkerProtocol.RunCodexSmokeTest:
                return new WorkerResponse(
                    true,
                    CodexSmokeTest: await codex.RunSmokeTestAsync(cancellationToken));

            case WorkerProtocol.CreateMission:
            {
                if (string.IsNullOrWhiteSpace(request.Goal))
                {
                    return Error(
                        WorkerErrorCodes.MissionGoalRequired,
                        "Mission goal is required.");
                }

                if (string.IsNullOrWhiteSpace(request.WorkspacePath) ||
                    !Directory.Exists(request.WorkspacePath))
                {
                    return Error(
                        WorkerErrorCodes.WorkspaceInvalid,
                        "A valid workspace directory is required.");
                }

                var snapshot = await orchestrator.CreateMissionAsync(
                    request.Goal,
                    request.WorkspacePath,
                    request.ExecutionMode,
                    cancellationToken);

                _ = ExecuteMissionSafelyAsync(snapshot.Mission.Id);
                return new WorkerResponse(true, Mission: snapshot);
            }

            case WorkerProtocol.GetMission:
            {
                if (string.IsNullOrWhiteSpace(request.MissionId))
                {
                    return Error(
                        WorkerErrorCodes.MissionIdRequired,
                        "Mission id is required.");
                }

                var snapshot = await store.GetAsync(
                    request.MissionId,
                    cancellationToken);

                return snapshot is null
                    ? Error(
                        WorkerErrorCodes.MissionNotFound,
                        "Mission not found.")
                    : new WorkerResponse(true, Mission: snapshot);
            }

            default:
                return Error(
                    WorkerErrorCodes.UnsupportedRequest,
                    $"Unsupported request type '{request.Type}'.");
        }
    }

    private static WorkerResponse Error(
        string code,
        string message) =>
        new(false, Error: message, ErrorCode: code);

    private async Task ExecuteMissionSafelyAsync(string missionId)
    {
        try
        {
            await orchestrator.RunMissionAsync(missionId);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Mission {missionId} execution stopped: {exception.Message}");
        }
    }
}
