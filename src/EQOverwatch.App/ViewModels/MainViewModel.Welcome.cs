using EQOverwatch.App.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>The overlays ticked in the first-run window to begin with.</summary>
    public static IReadOnlyList<OverlayKind> WelcomeOverlayDefaults { get; } =
        new[] { OverlayKind.Buffs, OverlayKind.Debuffs, OverlayKind.RaidTriggers, OverlayKind.Dps };

    /// <summary>
    /// Before the first-run window: every built-in overlay starts hidden, so none can cover that window (overlays stay on top and the
    /// window blocks clicks on them until it closes). Its choices then switch the picked ones on.
    /// </summary>
    public static void HideOverlaysForWelcome(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.BuffsOverlay = settings.BuffsOverlay with { IsVisible = false };
        settings.DebuffsOverlay = settings.DebuffsOverlay with { IsVisible = false };
        settings.SongsOverlay = settings.SongsOverlay with { IsVisible = false };
        settings.RaidTriggersOverlay = settings.RaidTriggersOverlay with { IsVisible = false };
        settings.ChChainOverlay = settings.ChChainOverlay with { IsVisible = false };
        settings.DpsOverlay = settings.DpsOverlay with { IsVisible = false };
        settings.TellsOverlay = settings.TellsOverlay with { IsVisible = false };
        settings.MobCardOverlay = settings.MobCardOverlay with { IsVisible = false };
        settings.FctDamageOverlay = settings.FctDamageOverlay with { IsVisible = false };
        settings.FctHealsOverlay = settings.FctHealsOverlay with { IsVisible = false };
    }

    /// <summary>
    /// The first-run window's choices: anonymous sharing turns every sharing switch on or off except Server First, which has its own
    /// choice (its warning was on that window, so it is not asked again). The EverQuest folder, when one was picked, is then applied as
    /// if chosen on Settings.
    /// </summary>
    public async Task ApplyWelcomeAsync(string? eqFolder, bool anonymousSharing, bool serverFirst, IReadOnlyCollection<OverlayKind>? overlays = null)
    {
        if (overlays is not null)
        {
            // The overlays were held back while the window was open; the ticked ones open now, unlocked to be dragged into place.
            foreach (OverlayKind kind in OverlayKindExtensions.All)
            {
                SetOverlayVisible(kind, overlays.Contains(kind));
            }
        }

        UseSharedDrops = anonymousSharing;
        ShareMyDrops = anonymousSharing;
        PoolNormalLootDrops = anonymousSharing;
        UseSharedLeveling = anonymousSharing;
        ShareMyLeveling = anonymousSharing;
        UseSharedMobStats = anonymousSharing;
        ShareMyMobStats = anonymousSharing;
        ShareResourceBonuses = anonymousSharing;
        CountMeIn = anonymousSharing;

        bool wasRestoring = _restoring;
        _restoring = true;
        ShareServerFirsts = serverFirst;
        _restoring = wasRestoring;
        UpdateShareSummaries();
        RequestSave();

        if (!string.IsNullOrWhiteSpace(eqFolder))
        {
            await ApplyEqFolderAsync(eqFolder);
        }
    }
}
