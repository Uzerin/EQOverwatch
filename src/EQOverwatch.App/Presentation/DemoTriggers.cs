using EQOverwatch.Core.Models;

namespace EQOverwatch.App.Presentation;

/// <summary>Two ready-made triggers (from the project's sample lines) for checking the overlays and audio end to end.</summary>
public static class DemoTriggers
{
    public const string SkinFadesLine = "Your protective skin fades.";
    public const string CompleteHealLine = "Ream begins to cast a spell. <Complete Heal>";

    public static IReadOnlyList<TriggerDefinition> All { get; } = new[]
    {
        new TriggerDefinition
        {
            Name = "Protective Skin Faded",
            OverlayCategory = TimerOverlayCategory.Buffs,
            Pattern = @"^Your protective skin fades\.$",
            Actions = TriggerActions.TextOverlay | TriggerActions.Audio,
            DurationSeconds = 6,
            DisplayText = "${Character}: Protective Skin Faded",
            AudioText = "${Character}, protective skin faded",
        },
        new TriggerDefinition
        {
            Name = "Complete Heal",
            OverlayCategory = TimerOverlayCategory.Buffs,
            Pattern = @"^(?<caster>\w+) begins to cast a spell\. <Complete Heal>$",
            Actions = TriggerActions.CountdownTimer,
            DurationSeconds = 10,
            DisplayText = "${caster}: Complete Heal",
        },
    };
}
