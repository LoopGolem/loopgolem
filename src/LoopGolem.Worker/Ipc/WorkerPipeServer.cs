using System.IO.Pipes;
using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Ipc;

public sealed class WorkerPipeServer(
    IMissionStore store,
    IMissionOrchestrator orchestrator,
    CodexCliService codex,
    MissionTelemetryService telemetry,
    ICodexWorkerAppServerTransport appServerTransport)
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
        var reader = new StreamReader(stream, leaveOpen: true);
        var writer = new StreamWriter(stream, leaveOpen: true)
        {
            AutoFlush = true
        };

        try
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (IOException)
            {
                // The client disconnected before sending a complete request.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            WorkerResponse response;

            try
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    response = Error(
                        WorkerErrorCodes.EmptyRequest,
                        "Empty request.");
                }
                else
                {
                    var request = JsonSerializer.Deserialize<WorkerRequest>(
                        line,
                        JsonOptions);

                    response = request is null
                        ? Error(
                            WorkerErrorCodes.InvalidRequest,
                            "Invalid request.")
                        : await HandleRequestAsync(
                            request,
                            cancellationToken);
                }
            }
            catch (JsonException exception)
            {
                response = Error(
                    WorkerErrorCodes.InvalidJson,
                    $"Invalid JSON: {exception.Message}");
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                response = Error(
                    WorkerErrorCodes.InternalError,
                    exception.Message);
            }

            await TryWriteResponseAsync(
                writer,
                response,
                cancellationToken);
        }
        finally
        {
            SafeDispose(writer);
            SafeDispose(reader);
        }
    }

    private static async Task TryWriteResponseAsync(
        StreamWriter writer,
        WorkerResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response, JsonOptions));

            await writer.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // A client can time out or close while a long-running request is
            // still being processed. That is a normal transport disconnect.
        }
        catch (ObjectDisposedException)
        {
            // The connection disappeared before the response could be flushed.
        }
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (IOException)
        {
            // StreamWriter.Dispose may flush and rediscover the broken pipe.
        }
        catch (ObjectDisposedException)
        {
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

            case WorkerProtocol.GetCodexAllowance:
                return new WorkerResponse(
                    true,
                    CodexAllowance:
                        await appServerTransport.ReadAllowanceAsync(
                            cancellationToken));

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

                MissionPolicy? policy = null;

                if (request.SessionReuse is { } sessionReuse ||
                    request.WorkerContext is not null ||
                    request.WorkerReasoning is not null ||
                    request.PauseAfterPlanning ||
                    !string.IsNullOrWhiteSpace(
                        request.FrozenPlannerResultJson))
                {
                    policy =
                        MissionPolicy.Default;

                    if (request.SessionReuse is { } reuse)
                    {
                        policy = policy with
                        {
                            SessionReuse = reuse
                        };
                    }

                    if (request.WorkerContext is { } workerContext)
                    {
                        policy = policy with
                        {
                            WorkerContext =
                                workerContext
                        };
                    }

                    if (request.WorkerReasoning is { } workerReasoning)
                    {
                        policy = policy with
                        {
                            WorkerReasoning =
                                workerReasoning
                        };
                    }

                    if (request.PauseAfterPlanning)
                    {
                        policy = policy with
                        {
                            PauseAfterPlanning = true
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(
                            request.FrozenPlannerResultJson))
                    {
                        policy = policy with
                        {
                            FrozenPlannerResultJson =
                                request.FrozenPlannerResultJson
                        };
                    }
                }

                var snapshot = await orchestrator.CreateMissionAsync(
                    request.Goal,
                    request.WorkspacePath,
                    request.ExecutionMode,
                    cancellationToken,
                    policy);

                _ = ExecuteMissionSafelyAsync(snapshot.Mission.Id);
                return new WorkerResponse(
                    true,
                    Mission: snapshot,
                    Telemetry: await telemetry.GetSummaryAsync(
                        snapshot,
                        cancellationToken));
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
                    : new WorkerResponse(
                        true,
                        Mission: snapshot,
                        Telemetry: await telemetry.GetSummaryAsync(
                            snapshot,
                            cancellationToken));
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
