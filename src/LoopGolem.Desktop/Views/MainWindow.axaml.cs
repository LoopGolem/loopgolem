using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LoopGolem.Core.Domain;
using LoopGolem.Desktop.Ipc;
using LoopGolem.Desktop.Localization;

namespace LoopGolem.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly WorkerPipeClient _workerClient = new();
    private CancellationTokenSource? _missionPolling;
    private string? _workspacePath;
    private bool _workerConnected;

    public MainWindow()
    {
        InitializeComponent();

        FlowDirection = LocalizationService.FlowDirection;
        ApplyLocalizedText();

        BrowseButton.Click += BrowseButton_Click;
        StartButton.Click += StartButton_Click;
        MissionGoalBox.TextChanged += (_, _) => UpdateStartButtonState();

        Opened += async (_, _) => await RefreshWorkerStatusAsync();
        Closed += (_, _) => _missionPolling?.Cancel();

        UpdateStartButtonState();
    }

    private void ApplyLocalizedText()
    {
        Title = LocalizationService.Get("AppTitle");
        TitleText.Text = LocalizationService.Get("AppTitle");
        SubtitleText.Text = LocalizationService.Get("AppSubtitle");
        WorkerLabel.Text = $"{LocalizationService.Get("Worker")}:";
        WorkspaceLabel.Text = LocalizationService.Get("Workspace");
        BrowseButton.Content = LocalizationService.Get("Browse");
        MissionGoalLabel.Text = LocalizationService.Get("MissionGoal");
        MissionGoalBox.PlaceholderText = LocalizationService.Get("MissionGoalHint");
        MissionStatusLabel.Text = $"{LocalizationService.Get("MissionStatus")}:";
        MissionStatusValue.Text = LocalizationService.Get("Ready");
        MissionResultLabel.Text = LocalizationService.Get("MissionResult");
        SettingsButton.Content = LocalizationService.Get("Settings");
        StartButton.Content = LocalizationService.Get("StartMission");
        MissionResultText.Text = string.Empty;
    }

    private async Task RefreshWorkerStatusAsync()
    {
        try
        {
            var response = await _workerClient.PingAsync();
            _workerConnected = response.Success;
        }
        catch
        {
            _workerConnected = false;
        }

        WorkerStatusText.Text = LocalizationService.Get(
            _workerConnected ? "Connected" : "Disconnected");

        if (!_workerConnected && string.IsNullOrWhiteSpace(MissionResultText.Text))
        {
            MissionResultText.Text = LocalizationService.Get("WorkerUnavailable");
        }

        UpdateStartButtonState();
    }

    private async void BrowseButton_Click(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = LocalizationService.Get("BrowseWorkspaceTitle"),
                AllowMultiple = false
            });

        var selected = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _workspacePath = selected;
        WorkspaceBox.Text = selected;
        UpdateStartButtonState();
    }

    private async void StartButton_Click(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (!_workerConnected)
        {
            await RefreshWorkerStatusAsync();
            if (!_workerConnected)
            {
                return;
            }
        }

        var goal = MissionGoalBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(goal) ||
            string.IsNullOrWhiteSpace(_workspacePath))
        {
            UpdateStartButtonState();
            return;
        }

        StartButton.IsEnabled = false;
        MissionResultText.Text = string.Empty;
        MissionStatusValue.Text = LocalizationService.Get("Planning");

        WorkerResponse response;
        try
        {
            response = await _workerClient.CreateMissionAsync(
                goal,
                _workspacePath);
        }
        catch
        {
            _workerConnected = false;
            WorkerStatusText.Text = LocalizationService.Get("Disconnected");
            MissionResultText.Text = LocalizationService.Get("WorkerUnavailable");
            UpdateStartButtonState();
            return;
        }

        if (!response.Success || response.Mission is null)
        {
            MissionStatusValue.Text = LocalizationService.Get("Failed");
            MissionResultText.Text = response.Error
                ?? LocalizationService.Get("Failed");
            UpdateStartButtonState();
            return;
        }

        _missionPolling?.Cancel();
        _missionPolling?.Dispose();
        _missionPolling = new CancellationTokenSource();

        try
        {
            await PollMissionAsync(
                response.Mission.Mission.Id,
                _missionPolling.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closed or a new mission replaced the polling loop.
        }

        UpdateStartButtonState();
    }

    private async Task PollMissionAsync(
        string missionId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkerResponse response;
            try
            {
                response = await _workerClient.GetMissionAsync(
                    missionId,
                    cancellationToken);
            }
            catch
            {
                _workerConnected = false;
                WorkerStatusText.Text = LocalizationService.Get("Disconnected");
                MissionResultText.Text = LocalizationService.Get("WorkerUnavailable");
                return;
            }

            if (!response.Success || response.Mission is null)
            {
                MissionStatusValue.Text = LocalizationService.Get("Failed");
                MissionResultText.Text = response.Error
                    ?? LocalizationService.Get("Failed");
                return;
            }

            var mission = response.Mission.Mission;
            MissionStatusValue.Text = GetMissionStatusText(mission.Status);

            if (!string.IsNullOrWhiteSpace(mission.Result))
            {
                MissionResultText.Text = mission.Result;
            }
            else if (!string.IsNullOrWhiteSpace(mission.Error))
            {
                MissionResultText.Text = mission.Error;
            }

            if (mission.Status is MissionStatus.Completed or MissionStatus.Failed)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    private static string GetMissionStatusText(MissionStatus status) =>
        LocalizationService.Get(status switch
        {
            MissionStatus.Created => "Ready",
            MissionStatus.Planning => "Planning",
            MissionStatus.Running => "Running",
            MissionStatus.WaitingForQuota => "WaitingForQuota",
            MissionStatus.WaitingForApproval => "WaitingForApproval",
            MissionStatus.Paused => "Paused",
            MissionStatus.Failed => "Failed",
            MissionStatus.Completed => "Completed",
            _ => "Status"
        });

    private void UpdateStartButtonState()
    {
        StartButton.IsEnabled =
            _workerConnected &&
            !string.IsNullOrWhiteSpace(_workspacePath) &&
            !string.IsNullOrWhiteSpace(MissionGoalBox.Text);
    }
}
