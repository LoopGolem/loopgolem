using System.IO.Pipes;
using System.Text.Json;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Ipc;

public sealed class WorkerPipeServer(
    IMissionStore store,
    IMissionOrchestrator orchestrator)
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
                response = new WorkerResponse(false, "Empty request.");
            }
            else
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions);
                response = request is null
                    ? new WorkerResponse(false, "Invalid request.")
                    : await HandleRequestAsync(request, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            response = new WorkerResponse(false, $"Invalid JSON: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new WorkerResponse(false, exception.Message);
        }

        await writer.WriteLineAsync(
            JsonSerializer.Serialize(response, JsonOptions));
    }

    private async Task<WorkerResponse> HandleRequestAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case WorkerProtocol.Ping:
                return new WorkerResponse(true, WorkerStatus: "ready");

            case WorkerProtocol.CreateMission:
            {
                if (string.IsNullOrWhiteSpace(request.Goal))
                {
                    return new WorkerResponse(false, "Mission goal is required.");
                }

                if (string.IsNullOrWhiteSpace(request.WorkspacePath) ||
                    !Directory.Exists(request.WorkspacePath))
                {
                    return new WorkerResponse(false, "A valid workspace directory is required.");
                }

                var snapshot = await orchestrator.CreateMissionAsync(
                    request.Goal,
                    request.WorkspacePath,
                    cancellationToken);

                _ = ExecuteMissionSafelyAsync(snapshot.Mission.Id);
                return new WorkerResponse(true, Mission: snapshot);
            }

            case WorkerProtocol.GetMission:
            {
                if (string.IsNullOrWhiteSpace(request.MissionId))
                {
                    return new WorkerResponse(false, "Mission id is required.");
                }

                var snapshot = await store.GetAsync(
                    request.MissionId,
                    cancellationToken);

                return snapshot is null
                    ? new WorkerResponse(false, "Mission not found.")
                    : new WorkerResponse(true, Mission: snapshot);
            }

            default:
                return new WorkerResponse(
                    false,
                    $"Unsupported request type '{request.Type}'.");
        }
    }

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
