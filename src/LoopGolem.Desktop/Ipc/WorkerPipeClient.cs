using System.IO.Pipes;
using System.Text.Json;
using LoopGolem.Core.Protocol;

namespace LoopGolem.Desktop.Ipc;

public sealed class WorkerPipeClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<WorkerResponse> PingAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new WorkerRequest(WorkerProtocol.Ping),
            cancellationToken);

    public Task<WorkerResponse> CreateMissionAsync(
        string goal,
        string workspacePath,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new WorkerRequest(
                WorkerProtocol.CreateMission,
                Goal: goal,
                WorkspacePath: workspacePath),
            cancellationToken);

    public Task<WorkerResponse> GetMissionAsync(
        string missionId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new WorkerRequest(
                WorkerProtocol.GetMission,
                MissionId: missionId),
            cancellationToken);

    private static async Task<WorkerResponse> SendAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        await using var pipe = new NamedPipeClientStream(
            ".",
            WorkerProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeout.Token);

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request, JsonOptions));

        var line = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("Worker returned an empty response.");
        }

        return JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions)
            ?? throw new IOException("Worker returned an invalid response.");
    }
}
