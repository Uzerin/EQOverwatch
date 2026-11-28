using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>A choice in an alert's "Show in" list: where it goes and its label.</summary>
public sealed record AlertOverlayChoice(AlertOverlay Value, string Label);

// Stun and lost-control alerts (a flashing banner like CAST CH NOW!, in the overlay you pick) and the food and drink alert.
public partial class MainViewModel
{
    /// <summary>A stun banner ends on "You are no longer stunned." or after this long.</summary>
    public static readonly TimeSpan StunLongest = TimeSpan.FromSeconds(10);

    /// <summary>A lost-control banner ends on "You have control of yourself again." or after this long.</summary>
    public static readonly TimeSpan LostControlLongest = TimeSpan.FromMinutes(3);

    /// <summary>The same character's stun is not spoken again sooner than this.</summary>
    public static readonly TimeSpan StunQuiet = TimeSpan.FromSeconds(10);

    /// <summary>The same character's food and drink alert is not repeated sooner than this.</summary>
    public static readonly TimeSpan FoodQuiet = TimeSpan.FromMinutes(5);

    /// <summary>How often a showing banner is put up again, which makes it flash (as CAST CH NOW! does).</summary>
    public static readonly TimeSpan FlashEvery = TimeSpan.FromSeconds(1);

    /// <summary>The overlays a stun or lost-control banner can go to.</summary>
    public static IReadOnlyList<AlertOverlayChoice> AlertOverlayChoices { get; } = new[]
    {
        new AlertOverlayChoice(AlertOverlay.FloatingText, "Floating text"),
        new AlertOverlayChoice(AlertOverlay.RaidTriggers, "Raid Triggers"),
        new AlertOverlayChoice(AlertOverlay.ChChain, "CH Chain"),
        new AlertOverlayChoice(AlertOverlay.Buffs, "Buffs"),
        new AlertOverlayChoice(AlertOverlay.Debuffs, "Debuffs"),
    };

    /// <summary><see cref="AlertOverlayChoices"/> for the Settings lists.</summary>
    public IReadOnlyList<AlertOverlayChoice> AlertOverlayOptions => AlertOverlayChoices;

    /// <summary>Show and say "MENDWELL STUNNED!" when one of your characters is stunned during a fight.</summary>
    [ObservableProperty]
    private bool _stunAlert = true;

    /// <summary>Show and say "OODLES LOST CONTROL!" when one of your characters is charmed, feared or mind controlled.</summary>
    [ObservableProperty]
    private bool _lostControlAlert = true;

    /// <summary>Say when one of your characters is out of food or drink, and show a banner when it is low.</summary>
    [ObservableProperty]
    private bool _foodAlert = true;

    [ObservableProperty]
    private AlertOverlay _stunAlertOverlay = AlertOverlay.FloatingText;

    [ObservableProperty]
    private AlertOverlay _lostControlAlertOverlay = AlertOverlay.FloatingText;

    partial void OnStunAlertChanged(bool value)
    {
        if (!value)
        {
            EndControlAlerts("stun|");
        }

        SaveUnlessRestoring();
    }

    partial void OnLostControlAlertChanged(bool value)
    {
        if (!value)
        {
            EndControlAlerts("control|");
        }

        SaveUnlessRestoring();
    }

    partial void OnFoodAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnStunAlertOverlayChanged(AlertOverlay value) => SaveUnlessRestoring();

    partial void OnLostControlAlertOverlayChanged(AlertOverlay value) => SaveUnlessRestoring();

    private sealed class ControlBanner
    {
        public required string Character;
        public required string Text;
        public required DateTime Started;
        public required DateTime Until;
        public required bool Stun;
        public DateTime NextFlash;
        public string? ShownText;
        public TimerOverlayCategory? ShownCategory;
    }

    private readonly object _controlGate = new();
    private readonly Dictionary<string, ControlBanner> _controlBanners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stun, lost-control and food lines of your own characters. True when the notice was one of them.</summary>
    internal bool OnControlNotice(string character, NoticeLine notice, DateTime now)
    {
        switch (notice.Kind)
        {
            case NoticeKind.Stunned:
                if (StunAlert && _ownCharacters.ContainsKey(character) && _pipeline.LiveDps.Snapshot.Active.Count > 0)
                {
                    StartControlBanner("stun|" + character, character, $"{character.ToUpperInvariant()} STUNNED!", now, now + StunLongest, stun: true);
                    if (!RecentlyAlerted("stun|" + character, now, StunQuiet))
                    {
                        _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, $"{character} stunned", now);
                    }
                }

                return true;

            case NoticeKind.StunEnded:
                EndControlAlerts("stun|" + character);
                return true;

            case NoticeKind.LostControl:
                if (LostControlAlert && _ownCharacters.ContainsKey(character))
                {
                    string text = $"{character.ToUpperInvariant()} LOST CONTROL!" + (notice.Name.Length > 0 ? $" ({notice.Name})" : string.Empty);
                    StartControlBanner("control|" + character, character, text, now, now + LostControlLongest, stun: false);
                    _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, $"{character} lost control", now);
                }

                return true;

            case NoticeKind.ControlBack:
                EndControlAlerts("control|" + character);
                return true;

            case NoticeKind.FoodLow or NoticeKind.FoodOut:
                if (FoodAlert && _ownCharacters.ContainsKey(character)
                    && !RecentlyAlerted((notice.Kind == NoticeKind.FoodOut ? "foodout|" : "foodlow|") + character, now, FoodQuiet))
                {
                    string text = $"{character} {notice.Name}";
                    _pipeline.Triggers.StartAlert(BoxAlertSourceId, BoxAlertSourceName, character, text, AggroBannerSeconds, now);
                    if (notice.Kind == NoticeKind.FoodOut)
                    {
                        _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, text, now);
                    }
                }

                return true;
        }

        return false;
    }

    private void StartControlBanner(string key, string character, string text, DateTime now, DateTime until, bool stun)
    {
        lock (_controlGate)
        {
            if (_controlBanners.Remove(key, out ControlBanner? old))
            {
                CancelShown(old);
            }

            _controlBanners[key] = new ControlBanner { Character = character, Text = text, Started = now, Until = until, Stun = stun };
        }

        TickControlAlerts(now);
    }

    /// <summary>Ends the banners whose key starts with <paramref name="prefix"/> (a whole key ends one).</summary>
    private void EndControlAlerts(string prefix)
    {
        lock (_controlGate)
        {
            foreach (string key in _controlBanners.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                CancelShown(_controlBanners[key]);
                _controlBanners.Remove(key);
            }
        }
    }

    private void CancelShown(ControlBanner banner)
    {
        if (banner.ShownText is { } shown)
        {
            _pipeline.Triggers.CancelAlert(BoxAlertSourceId, banner.Character, shown);
        }
    }

    /// <summary>The overlay category a banner goes to, or null for the floating text overlay (when it is switched on).</summary>
    private TimerOverlayCategory? CategoryFor(bool stun)
    {
        AlertOverlay where = stun ? StunAlertOverlay : LostControlAlertOverlay;
        return where switch
        {
            AlertOverlay.FloatingText when IsFctDamageOverlayVisible => null,
            AlertOverlay.ChChain => TimerOverlayCategory.ChChain,
            AlertOverlay.Buffs => TimerOverlayCategory.Buffs,
            AlertOverlay.Debuffs => TimerOverlayCategory.Debuffs,
            _ => TimerOverlayCategory.RaidTriggers, // also the floating text choice while that overlay is off
        };
    }

    /// <summary>Puts the showing banners up again each second (so they flash) and ends the ones past their time. Called from <see cref="Refresh"/>.</summary>
    public void TickControlAlerts(DateTime now)
    {
        lock (_controlGate)
        {
            if (_controlBanners.Count == 0)
            {
                return;
            }

            foreach ((string key, ControlBanner banner) in _controlBanners.ToList())
            {
                if (now >= banner.Until)
                {
                    CancelShown(banner);
                    _controlBanners.Remove(key);
                    continue;
                }

                TimerOverlayCategory? category = CategoryFor(banner.Stun);
                if (category != banner.ShownCategory && banner.ShownText is not null)
                {
                    CancelShown(banner);
                    banner.ShownText = null;
                    banner.NextFlash = default;
                }

                banner.ShownCategory = category;
                if (category is { } shownIn && now >= banner.NextFlash)
                {
                    // A fresh banner every second keeps it flashing; the text is the same, so it replaces the one before.
                    banner.NextFlash = now + FlashEvery;
                    banner.ShownText = banner.Text;
                    _pipeline.Triggers.StartAlert(BoxAlertSourceId, BoxAlertSourceName, banner.Character, banner.Text,
                        FlashEvery.TotalSeconds + 0.5, now, shownIn);
                }
            }
        }
    }

    /// <summary>The banners for the floating text overlay now, with when each started (it flashes them).</summary>
    public IReadOnlyList<(string Text, DateTime Started)> FloatingBanners(DateTime now)
    {
        lock (_controlGate)
        {
            return _controlBanners.Values
                .Where(b => now < b.Until && b.ShownCategory is null && CategoryFor(b.Stun) is null)
                .OrderBy(b => b.Started)
                .Select(b => (b.Text, b.Started))
                .ToList();
        }
    }
}
