namespace LoopGolem.Worker.Infrastructure;

public static class AppPaths
{
    public const string StateDirectoryEnvironmentVariable =
        "LOOPGOLEM_STATE_DIR";

    public static string GetStateDirectory()
    {
        var configured =
            Environment.GetEnvironmentVariable(
                StateDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(localAppData, "LoopGolem");
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
        {
            return Path.Combine(xdgStateHome, "loopgolem");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "loopgolem");
    }

    public static string GetDatabasePath() =>
        Path.Combine(GetStateDirectory(), "loopgolem.db");
}
