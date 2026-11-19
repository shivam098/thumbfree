using System.Windows;
using ThumbFree.Core;

namespace ThumbFree.App.UI;

/// <summary>Download, cancel and delete for every model button, with the big-model question (PLAN 3.1) first.</summary>
internal static class ModelActions
{
    /// <summary>Starts downloading <paramref name="spec"/>; a big model first asks about size, memory and data. False when the answer is no.</summary>
    public static bool Download(AppServices services, ModelSpec spec, Window? owner)
    {
        if (ModelConsent.Needed(spec))
        {
            var environment = services.Environment;
            var question = new Confirmation(
                ModelConsent.Title(spec),
                ModelConsent.Message(spec, environment.TotalMemoryBytes, environment.IsMeteredConnection()),
                $"Download ({ModelConsent.Mb(spec.Bytes)})",
                "Not now");
            if (!environment.Confirm(owner, question))
            {
                return false;
            }
        }

        _ = services.Models.Start(spec);
        return true;
    }

    /// <summary>Deletes a model; the one in use asks first, since dictation stops until another is ready.</summary>
    public static void Delete(AppServices services, ModelSpec spec, Window? owner)
    {
        if (services.Current.SelectedModel() == spec && !services.Environment.Confirm(owner, new Confirmation(
            $"Delete “{ModelConsent.ShortName(spec)}”?",
            "It's the model dictation uses now, so Ctrl+Win won't work until you download it again or choose another one.",
            "Delete",
            Danger: true)))
        {
            return;
        }

        services.Models.Delete(spec);
    }

    /// <summary>Starts a small model's download when it's chosen and not here yet (big ones wait for Download and its question).</summary>
    public static void DownloadIfSmall(AppServices services, ModelSpec spec)
    {
        if (!ModelConsent.Needed(spec) && services.Models.State(spec).Status == ModelStatus.NotInstalled)
        {
            _ = services.Models.Start(spec);
        }
    }

    /// <summary>"Live", "Standard" or "Accurate".</summary>
    public static string TierName(Tier tier) => tier.ToString();

    /// <summary>"English" or "Hindi + English".</summary>
    public static string LanguageName(Language language) => language == Language.Hinglish ? "Hindi + English" : "English";
}
