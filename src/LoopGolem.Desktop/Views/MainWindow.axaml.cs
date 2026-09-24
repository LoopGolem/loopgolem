using Avalonia.Controls;
using LoopGolem.Desktop.Localization;

namespace LoopGolem.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        FlowDirection = LocalizationService.FlowDirection;
        Title = LocalizationService.Get("AppTitle");
        TitleText.Text = LocalizationService.Get("AppTitle");
        SubtitleText.Text = LocalizationService.Get("AppSubtitle");
        WorkspaceLabel.Text = LocalizationService.Get("Workspace");
        BrowseButton.Content = LocalizationService.Get("Browse");
        MissionGoalLabel.Text = LocalizationService.Get("MissionGoal");
        MissionGoalBox.PlaceholderText = LocalizationService.Get("MissionGoalHint");
        StatusLabel.Text = $"{LocalizationService.Get("Status")}:";
        StatusValueText.Text = LocalizationService.Get("Ready");
        SettingsButton.Content = LocalizationService.Get("Settings");
        StartButton.Content = LocalizationService.Get("StartMission");
    }
}
