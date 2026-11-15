using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The profile window's Persona drop-down, focus choices per persona, and the inventory picture.</summary>
public partial class MainViewModelTests
{
    [Fact]
    public async Task The_persona_drop_down_switches_skills_spellbook_and_focus_and_the_class_follows_the_persona()
    {
        using var h = new Harness();
        await h.Vm.ApplyEqFolderAsync(h.Dir);
        File.WriteAllText(Path.Combine(h.Dir, "Ream_frostreaver-CLR-Spellbook.txt"), "60\tComplete Heal\n");
        File.WriteAllText(Path.Combine(h.Dir, "Ream_frostreaver-Inventory.txt"),
            "Location\tName\tID\tCount\tSlots\nHead\tCrown of the Kromzek Kings\t25194\t1\t6\nHead-Slot1\tEmpty\t0\t0\t0\nGeneral 1\tLarge Bag\t1\t1\t2\nGeneral 1-Slot1\tBone Chips\t2\t12\t6\nGeneral 1-Slot2\tEmpty\t0\t0\t0\n");
        h.Dialogs.LogFiles = new[] { h.CreateLog("Ream") };
        h.Vm.AddLogCommand.Execute(null);
        WatchedLogConfig ream = h.Vm.Logs.Single();
        string server = ream.ServerName;

        DateTime now = DateTime.UtcNow;
        h.Pipeline.Progress.OnLine("Ream", server, "You have become better at Meditate! (235)", now);
        h.Pipeline.Progress.OnLine("Ream", server, "Your Cleric equipment set has been saved.", now);
        h.Pipeline.Progress.OnLine("Ream", server, "You successfully loaded your Bard equipment set.", now);
        h.Pipeline.Progress.OnLine("Ream", server, "[40 Bard] Ream (Half Elf) ZONE: Velketor's Labyrinth", now);
        h.Pipeline.Progress.OnLine("Ream", server, "You have become better at Singing! (180)", now);
        Assert.Equal(("Bard", 40), (h.Pipeline.Roster.GetClass("Ream", server), h.Pipeline.Roster.GetLevel("Ream", server)));

        h.Vm.OpenProfileCommand.Execute(ream);
        CharacterDataViewModel window = h.CharacterPresenter.Shown.Single();
        Assert.Equal(new[] { "Bard 40 (current)", "Cleric" }, window.PersonaChoices.Select(p => p.Label));
        Assert.Equal("Bard", window.SelectedPersona!.Persona);
        Assert.Equal("Singing", window.SkillRows.Single().Skill);
        Assert.Empty(window.SpellbookRows);
        Assert.Contains("/outputfile spellbook", window.SpellbookNote);

        window.Focus!.AaPercent = 20; // the bard's choice
        window.SelectedPersona = window.PersonaChoices.Single(p => p.Persona == "Cleric");
        Assert.Equal("Meditate", window.SkillRows.Single().Skill);
        Assert.Equal("Complete Heal", window.SpellbookRows.Single().Name);
        Assert.Equal(string.Empty, window.SpellbookNote);
        Assert.Equal(0, h.Vm.GetDurationProfile("Ream", server, "Cleric").AaPercent);
        Assert.Equal(20, h.Vm.GetDurationProfile("Ream", server, "Bard").AaPercent);

        // The inventory picture: worn gear with its socket, and the first bag open with its two slots.
        InventoryPanelViewModel panel = window.InventoryPanel;
        Assert.Equal(new[] { false }, panel.Worn.Single(s => s.Label == "Head").Augments);
        Assert.True(panel.Containers[0].IsOpen);
        Assert.Equal(new[] { "BC", "" }, panel.OpenContents.Select(s => s.Initials));
        Assert.Equal("12", panel.OpenContents[0].CountText);
        panel.OpenCommand.Execute(panel.Containers[0]);
        Assert.False(panel.HasOpenBag);
    }
}
