using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Desktop.Ipc;
using LoopGolem.Desktop.Localization;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly WorkerPipeClient _workerClient = new();
    private readonly DispatcherTimer _workerHeartbeat;
    private CancellationTokenSource? _missionPolling;
    private string? _activeMissionId;
    private string? _workspacePath;
    private bool _workerConnected;
    private bool _codexReady;
    private bool _codexChecked;
    private bool _refreshingWorkerStatus;

    public MainWindow()
    {
        InitializeComponent();

        FlowDirection = LocalizationService.FlowDirection;
        ApplyLocalizedText();

        BrowseButton.Click += BrowseButton_Click;
        StartButton.Click += StartButton_Click;
        MissionGoalBox.TextChanged += (_, _) => UpdateStartButtonState();

        _workerHeartbeat = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _workerHeartbeat.Tick += async (_, _) => await RefreshWorkerStatusAsync();

        Opened += async (_, _) =>
        {
            await RefreshWorkerStatusAsync();
            _workerHeartbeat.Start();
        };
        Closed += (_, _) =>
        {
            _workerHeartbeat.Stop();
            _missionPolling?.Cancel();
        };

        UpdateStartButtonState();
    }

    private void ApplyLocalizedText()
    {
        Title = LocalizationService.Get("AppTitle");
        TitleText.Text = LocalizationService.Get("AppTitle");
        SubtitleText.Text = LocalizationService.Get("AppSubtitle");
        WorkerLabel.Text = $"{LocalizationService.Get("Worker")}:";
        CodexLabel.Text = $"{LocalizationService.Get("Codex")}:";
        CodexStatusText.Text = LocalizationService.Get("CodexNotChecked");
        UseCodexCheckBox.Content = LocalizationService.Get("UseCodex");
        WorkspaceLabel.Text = LocalizationService.Get("Workspace");
        BrowseButton.Content = LocalizationService.Get("Browse");
        MissionGoalLabel.Text = LocalizationService.Get("MissionGoal");
        MissionGoalBox.PlaceholderText = LocalizationService.Get("MissionGoalHint");
        MissionStatusLabel.Text = $"{LocalizationService.Get("MissionStatus")}:";
        MissionStatusValue.Text = LocalizationService.Get("Ready");
        TasksLabel.Text = LocalizationService.Get("Tasks");
        MissionResultLabel.Text = LocalizationService.Get("MissionResult");
        SettingsButton.Content = LocalizationService.Get("Settings");
        StartButton.Content = LocalizationService.Get("StartMission");
        MissionResultText.Text = string.Empty;
        RenderTasks([]);
    }

    private async Task RefreshWorkerStatusAsync()
    {
        if (_refreshingWorkerStatus)
        {
            return;
        }

        _refreshingWorkerStatus = true;

        try
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

            var unavailableText = LocalizationService.Get("WorkerUnavailable");
            if (!_workerConnected && string.IsNullOrWhiteSpace(MissionResultText.Text))
            {
                MissionResultText.Text = unavailableText;
            }
            else if (_workerConnected &&
                     string.Equals(
                         MissionResultText.Text,
                         unavailableText,
                         StringComparison.Ordinal))
            {
                MissionResultText.Text = string.Empty;
            }

            if (_workerConnected && !_codexChecked)
            {
                await RefreshCodexStatusAsync();
            }

            UpdateStartButtonState();
        }
        finally
        {
            _refreshingWorkerStatus = false;
        }
    }

    private async Task RefreshCodexStatusAsync()
    {
        _codexChecked = true;
        CodexRuntimeStatus? status = null;

        try
        {
            var response = await _workerClient.GetCodexStatusAsync();
            status = response.CodexStatus;
            _codexReady =
                response.Success &&
                status is
                {
                    Available: true,
                    ChatGptAuthenticated: true,
                    State: CodexRuntimeState.Ready
                };
        }
        catch
        {
            _codexReady = false;
        }

        CodexStatusText.Text = status is null
            ? LocalizationService.Get("CodexUnavailable")
            : LocalizationService.Get(status.State switch
            {
                CodexRuntimeState.Ready when status.Runtime == "wsl" =>
                    "CodexReadyWsl",
                CodexRuntimeState.Ready =>
                    "CodexReady",
                CodexRuntimeState.WslDistributionMissing =>
                    "CodexWslRequired",
                CodexRuntimeState.CodexCliMissing when status.Runtime == "wsl" =>
                    "CodexCliMissingWsl",
                CodexRuntimeState.AuthenticationRequired when status.Runtime == "wsl" =>
                    "CodexAuthRequiredWsl",
                CodexRuntimeState.AuthenticationRequired =>
                    "CodexAuthRequired",
                _ =>
                    "CodexUnavailable"
            });

        ToolTip.SetTip(
            CodexStatusText,
            status?.Message ?? LocalizationService.Get("CodexUnavailable"));

        UseCodexCheckBox.IsEnabled = _codexReady;

        if (!_codexReady)
        {
            UseCodexCheckBox.IsChecked = false;
        }
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
        MissionStatusValue.Text = LocalizationService.Get("Ready");
        MissionResultText.Text = string.Empty;
        RenderTasks([]);
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
        RenderTasks([]);
        MissionStatusValue.Text = LocalizationService.Get("Planning");

        WorkerResponse response;
        try
        {
            response = await _workerClient.CreateMissionAsync(
                goal,
                _workspacePath,
                UseCodexCheckBox.IsChecked == true
                    ? MissionExecutionMode.Codex
                    : MissionExecutionMode.ValidateOnly);
        }
        catch
        {
            _workerConnected = false;
            WorkerStatusText.Text = LocalizationService.Get("Disconnected");
            MissionStatusValue.Text = LocalizationService.Get("NotStarted");
            MissionResultText.Text = LocalizationService.Get("WorkerUnavailable");
            UpdateStartButtonState();
            return;
        }

        if (!response.Success || response.Mission is null)
        {
            MissionStatusValue.Text = LocalizationService.Get("NotStarted");
            MissionResultText.Text = GetWorkerErrorText(response);
            UpdateStartButtonState();
            return;
        }

        RenderTasks(response.Mission.Tasks);
        _activeMissionId = response.Mission.Mission.Id;

        _missionPolling?.Cancel();
        _missionPolling?.Dispose();
        _missionPolling = new CancellationTokenSource();

        try
        {
            await PollMissionAsync(
                _activeMissionId,
                _missionPolling.Token);
        }
        catch (OperationCanceledException)
        {
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
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _workerConnected = false;
                WorkerStatusText.Text = LocalizationService.Get("Disconnected");

                // Keep the mission id and stay attached. The Worker is designed
                // to outlive transient client disconnects and can also restart
                // and resume persisted missions. Poll again until it returns.
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
                continue;
            }

            if (!response.Success || response.Mission is null)
            {
                MissionStatusValue.Text = LocalizationService.Get("Failed");
                MissionResultText.Text = GetWorkerErrorText(response);
                return;
            }

            var mission = response.Mission.Mission;
            MissionStatusValue.Text = GetMissionStatusText(mission.Status);
            RenderTasks(response.Mission.Tasks);

            if (!string.IsNullOrWhiteSpace(mission.Result))
            {
                MissionResultText.Text = BuildDisplayResult(response.Mission.Tasks);
            }
            else if (!string.IsNullOrWhiteSpace(mission.Error))
            {
                MissionResultText.Text = mission.Error;
            }

            if (mission.Status is
                MissionStatus.Completed or
                MissionStatus.Failed or
                MissionStatus.NeedsHumanAttention)
            {
                _activeMissionId = null;
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    private void RenderTasks(IReadOnlyList<MissionTask> tasks)
    {
        TasksPanel.Children.Clear();

        foreach (var task in tasks.OrderBy(task => task.Sequence))
        {
            var row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(0, 2)
            };

            var icon = new TextBlock
            {
                Text = GetTaskStatusSymbol(task.Status),
                Width = 24,
                FontWeight = task.Status == DomainTaskStatus.Running
                    ? Avalonia.Media.FontWeight.Bold
                    : Avalonia.Media.FontWeight.Normal
            };
            DockPanel.SetDock(icon, Dock.Left);

            var status = new TextBlock
            {
                Text = GetTaskStatusDisplayText(task),
                Opacity = 0.72,
                Margin = new Thickness(12, 0, 18, 0)
            };
            DockPanel.SetDock(status, Dock.Right);

            var title = new TextBlock
            {
                Text = GetTaskTitle(task),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            row.Children.Add(icon);
            row.Children.Add(status);
            row.Children.Add(title);
            TasksPanel.Children.Add(row);
        }
    }

    private static string GetTaskTitle(MissionTask task)
    {
        if (task.Kind is MissionTaskKind.DeterministicWork ||
            (task.Kind == MissionTaskKind.AgentWork &&
             task.Definition?.Executor == PlannedExecutorKinds.LunaLow))
        {
            return task.Title;
        }

        return LocalizationService.Get(task.Kind switch
        {
            MissionTaskKind.InspectWorkspace => "TaskInspectWorkspace",
            MissionTaskKind.DiscoverProjects => "TaskDiscoverProjects",
            MissionTaskKind.PlanMission => "TaskPlanMission",
            MissionTaskKind.ValidateMission => "TaskValidateMission",
            MissionTaskKind.AgentWork => "TaskAgentWork",
            MissionTaskKind.InspectGitChanges => "TaskInspectGitChanges",
            MissionTaskKind.BuildDotNet => "TaskBuildDotNet",
            _ => task.Title
        });
    }

    private static string GetTaskStatusDisplayText(
        MissionTask task)
    {
        var status = GetTaskStatusText(task.Status);

        if (task.TokenUsage is { } usage)
        {
            return $"{status} · {FormatTokenCount(usage.TotalTokens)}";
        }

        return task.Status == DomainTaskStatus.Completed &&
               !TaskConsumesModel(task)
            ? $"{status} · {FormatTokenCount(0)}"
            : status;
    }

    private static bool TaskConsumesModel(
        MissionTask task) =>
        task.Kind is
            MissionTaskKind.PlanMission or
            MissionTaskKind.ValidateMission ||
        task.Kind == MissionTaskKind.AgentWork &&
        task.Definition?.Executor ==
            PlannedExecutorKinds.LunaLow;

    private static string FormatTokenCount(long tokenCount) =>
        $"{tokenCount.ToString("N0", LocalizationService.CurrentCulture)} " +
        LocalizationService.Get("Tokens");

    private static string GetTaskStatusText(DomainTaskStatus status) =>
        LocalizationService.Get(status switch
        {
            DomainTaskStatus.Planned => "Planned",
            DomainTaskStatus.Ready => "Ready",
            DomainTaskStatus.Running => "Running",
            DomainTaskStatus.Verifying => "Verifying",
            DomainTaskStatus.Retrying => "Retrying",
            DomainTaskStatus.Escalated => "Escalated",
            DomainTaskStatus.Completed => "Completed",
            DomainTaskStatus.Blocked => "Blocked",
            DomainTaskStatus.Failed => "Failed",
            _ => "Status"
        });

    private static string GetTaskStatusSymbol(DomainTaskStatus status) =>
        status switch
        {
            DomainTaskStatus.Completed => "✓",
            DomainTaskStatus.Failed => "✕",
            DomainTaskStatus.Running => "●",
            DomainTaskStatus.Verifying => "◐",
            DomainTaskStatus.Retrying => "↻",
            DomainTaskStatus.Blocked => "!",
            DomainTaskStatus.Escalated => "↑",
            _ => "○"
        };

    private static string BuildDisplayResult(
        IEnumerable<MissionTask> tasks)
    {
        var materialized = tasks
            .OrderBy(task => task.Sequence)
            .ToArray();

        var lines = materialized
            .Where(task => !string.IsNullOrWhiteSpace(task.Result))
            .Select(task => $"{GetTaskTitle(task)}: {task.Result}")
            .ToList();

        var totalTokens = materialized.Sum(
            task => task.TokenUsage?.TotalTokens ?? 0);

        if (totalTokens > 0)
        {
            lines.Add(
                $"{LocalizationService.Get("TotalTokens")}: " +
                FormatTokenCount(totalTokens));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static string GetWorkerErrorText(WorkerResponse response) =>
        response.ErrorCode switch
        {
            WorkerErrorCodes.WorkspaceInvalid =>
                LocalizationService.Get("WorkspaceInvalid"),
            WorkerErrorCodes.MissionGoalRequired =>
                LocalizationService.Get("MissionGoalRequired"),
            _ => response.Error ?? LocalizationService.Get("CouldNotStartMission")
        };

    private static string GetMissionStatusText(MissionStatus status) =>
        LocalizationService.Get(status switch
        {
            MissionStatus.Created => "Ready",
            MissionStatus.Planning => "Planning",
            MissionStatus.Running => "Running",
            MissionStatus.WaitingForQuota => "WaitingForQuota",
            MissionStatus.WaitingForApproval => "WaitingForApproval",
            MissionStatus.Paused => "Paused",
            MissionStatus.NeedsHumanAttention => "NeedsHumanAttention",
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
