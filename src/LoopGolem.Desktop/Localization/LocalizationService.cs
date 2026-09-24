using System.Globalization;
using System.Resources;
using Avalonia.Media;

namespace LoopGolem.Desktop.Localization;

public static class LocalizationService
{
    private static readonly ResourceManager Resources =
        new("LoopGolem.Desktop.Localization.Strings", typeof(LocalizationService).Assembly);

    private static readonly IReadOnlyDictionary<string, string> Supported =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "en-US", ["pt"] = "pt-BR", ["es"] = "es", ["de"] = "de",
            ["it"] = "it", ["fr"] = "fr", ["he"] = "he", ["ar"] = "ar",
            ["fa"] = "fa", ["ja"] = "ja", ["ko"] = "ko", ["ru"] = "ru"
        };

    public static CultureInfo CurrentCulture { get; } = Resolve(CultureInfo.CurrentUICulture);

    public static FlowDirection FlowDirection =>
        CurrentCulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static string Get(string key) =>
        Resources.GetString(key, CurrentCulture)
        ?? Resources.GetString(key, CultureInfo.GetCultureInfo("en-US"))
        ?? key;

    private static CultureInfo Resolve(CultureInfo requested)
    {
        if (Supported.Values.Contains(requested.Name, StringComparer.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo(requested.Name);
        }

        return Supported.TryGetValue(requested.TwoLetterISOLanguageName, out var name)
            ? CultureInfo.GetCultureInfo(name)
            : CultureInfo.GetCultureInfo("en-US");
    }
}
