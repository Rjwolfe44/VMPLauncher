using Microsoft.Extensions.Logging;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.App.Services;

/// <summary>
/// Applies the player's texture-quality setting to a profile's settings.cfg before KSP starts (and right after a profile is
/// seeded from the player's own KSP). Records that Auto already ran for a profile so it never overrides a later choice.
/// Never throws: a failure is one log line and the game still starts.
/// </summary>
public sealed class TextureQualityService
{
    public const string NoticeText = "Texture quality was set to Half to save memory. Change it in Settings.";

    private readonly AppSettingsStore _settings;
    private readonly ILogger<TextureQualityService> _log;

    public TextureQualityService(AppSettingsStore settings, ILogger<TextureQualityService> log)
    {
        _settings = settings;
        _log = log;
    }

    /// <param name="profileId">Stable id of the profile (the pack's profile id, or a setup id).</param>
    /// <param name="freshlySeeded">The folder was just created from the player's KSP, so an earlier Auto record for this id no longer applies.</param>
    public TextureQualityOutcome Apply(string profileId, string gameDir, bool freshlySeeded = false)
    {
        try
        {
            if (freshlySeeded && _settings.Current.TextureQualityAutoDone.Contains(profileId))
            {
                _settings.Update(s => s with { TextureQualityAutoDone = s.TextureQualityAutoDone.Where(p => p != profileId).ToList() });
            }
            var s = _settings.Current;
            var policy = TextureQualitySetting.FromId(s.TextureQualityPolicy);
            var done = s.TextureQualityAutoDone.Contains(profileId);
            var outcome = TextureQualitySetting.Apply(gameDir, policy, done);
            if (outcome.Status is TextureQualityStatus.Failed or TextureQualityStatus.ReadOnly or TextureQualityStatus.Unsupported)
            {
                _log.LogWarning("{Message}", outcome.Message);
            }
            else
            {
                _log.LogInformation("{Message}", outcome.Message);
            }
            var notice = policy == TextureQualityPolicy.Auto && outcome.Changed;
            if (outcome.RecordAutoDone && !done || notice)
            {
                _settings.Update(c => c with
                {
                    TextureQualityAutoDone = outcome.RecordAutoDone && !c.TextureQualityAutoDone.Contains(profileId) ? c.TextureQualityAutoDone.Append(profileId).ToList() : c.TextureQualityAutoDone,
                    TextureQualityNotice = c.TextureQualityNotice || notice,
                });
            }
            if (notice)
            {
                _log.LogInformation("{Notice}", NoticeText);
            }
            return outcome;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Texture quality: skipped");
            return new TextureQualityOutcome(TextureQualityStatus.Failed, null, null, false, ex.Message);
        }
    }
}
