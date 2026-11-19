using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>Alt+Shift+H and the tray: the dictation language lives in Settings, and each language keeps its own model.</summary>
internal static class LanguageSwitch
{
    /// <summary>English ⇄ Hindi + English. Only the language changes: the tier chosen for each stays as it is.</summary>
    public static void Toggle(SettingsStore settings) =>
        settings.Update(s => s.Language = s.Language == Language.Hinglish ? Language.English : Language.Hinglish);
}
