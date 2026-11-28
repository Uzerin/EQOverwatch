using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.App.ViewModels;

// Release 81: follow and pet buff alerts, rolls and linkdead players on the Tells overlay, level-up notifications and the bonus XP notice.
public partial class MainViewModel
{
    /// <summary>The same character's follow alert is not repeated sooner than this.</summary>
    public static readonly TimeSpan FollowQuiet = TimeSpan.FromSeconds(20);

    /// <summary>A Windows notification when one of your characters gains a level while EverQuest is not the active window.</summary>
    [ObservableProperty]
    private bool _levelUpToast = true;

    /// <summary>Speak "X stopped following" when one of your characters stops auto-following.</summary>
    [ObservableProperty]
    private bool _followAlert = true;

    /// <summary>Speak "pet X faded" when a buff on one of your pets wears off.</summary>
    [ObservableProperty]
    private bool _petBuffAlert;

    /// <summary>Show /random rolls and players gone linkdead on the Tells overlay for 30 seconds.</summary>
    [ObservableProperty]
    private bool _tellsOverlayNotices = true;

    /// <summary>"Bonus XP looks active (+50%)" on the Leveling tab while your kills give well over the usual experience, else empty.</summary>
    [ObservableProperty]
    private string _bonusXpText = string.Empty;

    /// <summary>
    /// Raised on the tailing thread for a notification that only shows while EverQuest is not the active window (a level-up) or always
    /// (bonus XP started). The app shows it on the UI thread.
    /// </summary>
    public event Action<NoticeInfo, bool>? LiveNoticeRequested;

    private readonly BonusXpDetector _bonusXp = new();

    partial void OnLevelUpToastChanged(bool value) => SaveUnlessRestoring();

    partial void OnFollowAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnPetBuffAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnTellsOverlayNoticesChanged(bool value) => SaveUnlessRestoring();

    private void InitializeNotices()
    {
        _pipeline.LiveDps.Notice += OnNotice;
        _pipeline.MobExperience.LiveKill += _bonusXp.Observe;
        _bonusXp.Changed += active =>
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            BonusXpText = active
                ? $"Bonus XP looks active (+{((_bonusXp.Boost - 1) * 100).ToString("0", culture)}% over your usual kills)."
                : string.Empty;
            if (active)
            {
                LiveNoticeRequested?.Invoke(new NoticeInfo("Bonus XP looks active", "Your last kills gave well over the usual experience."), false);
            }
        };
    }

    /// <summary>The bonus XP detector, for tests.</summary>
    public BonusXpDetector BonusXp => _bonusXp;

    internal void OnNotice(string character, NoticeLine notice)
    {
        DateTime now = BoxAlertClock();
        if (OnControlNotice(character, notice, now))
        {
            return;
        }

        switch (notice.Kind)
        {
            case NoticeKind.FollowBroke when FollowAlert && _ownCharacters.ContainsKey(character):
                if (!RecentlyAlerted("follow|" + character, now, FollowQuiet))
                {
                    _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, $"{character} stopped following", now);
                }

                break;

            case NoticeKind.PetBuffFaded when PetBuffAlert && _ownCharacters.ContainsKey(character):
                _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, $"pet {notice.Name} faded", now);
                break;

            case NoticeKind.Linkdead when TellsOverlayNotices && !_ownCharacters.ContainsKey(notice.Name):
                _pipeline.Notices.Add("linkdead", $"{notice.Name} went linkdead", now);
                break;

            case NoticeKind.Roll when TellsOverlayNotices:
                CultureInfo culture = CultureInfo.CurrentCulture;
                _pipeline.Notices.Add("roll",
                    $"Roll  {notice.Name}  {notice.Value.ToString(culture)}  ({notice.Low.ToString(culture)}–{notice.High.ToString(culture)})", now);
                break;

            case NoticeKind.LevelUp when LevelUpToast && _ownCharacters.ContainsKey(character):
                LiveNoticeRequested?.Invoke(new NoticeInfo($"{character} reached level {notice.Value.ToString(CultureInfo.CurrentCulture)}", "Ding!"), true);
                break;
        }
    }
}
