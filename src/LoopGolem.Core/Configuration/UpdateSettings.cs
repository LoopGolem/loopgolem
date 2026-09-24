namespace LoopGolem.Core.Configuration;

public enum UpdateChannel
{
    Stable,
    Preview
}

public sealed record UpdateSettings(
    bool CheckAutomatically = true,
    bool DownloadAutomatically = false,
    bool InstallAutomatically = false,
    UpdateChannel Channel = UpdateChannel.Stable);
