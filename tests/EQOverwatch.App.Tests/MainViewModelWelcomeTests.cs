using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public async Task Welcome_turns_anonymous_sharing_off_but_server_first_on_without_asking_again()
    {
        using var h = new Harness();
        string eq = MakeEqFolder(h.Dir, "eqlog_Tankard_frostreaver.txt");

        await h.Vm.ApplyWelcomeAsync(eq, anonymousSharing: false, serverFirst: true);

        Assert.False(h.Vm.ShareMyDrops);
        Assert.False(h.Vm.UseSharedDrops);
        Assert.False(h.Vm.ShareMyLeveling);
        Assert.False(h.Vm.ShareMyMobStats);
        Assert.False(h.Vm.ShareResourceBonuses);
        Assert.False(h.Vm.CountMeIn);
        Assert.True(h.Vm.ShareServerFirsts);
        Assert.Empty(h.Dialogs.Questions); // the warning was already on the welcome window
        Assert.Equal(eq, h.Vm.EqFolderPath);
        Assert.Contains(h.Vm.Logs, l => l.CharacterName == "Tankard");
    }

    [Fact]
    public async Task Welcome_with_anonymous_sharing_turns_every_anonymous_switch_on()
    {
        using var h = new Harness();
        h.Vm.ShareMyLeveling = false;
        h.Vm.ShareMyMobStats = false;

        await h.Vm.ApplyWelcomeAsync(null, anonymousSharing: true, serverFirst: false);

        Assert.True(h.Vm.ShareMyLeveling);
        Assert.True(h.Vm.UseSharedLeveling);
        Assert.True(h.Vm.ShareMyMobStats);
        Assert.True(h.Vm.ShareMyDrops);
        Assert.True(h.Vm.PoolNormalLootDrops);
        Assert.True(h.Vm.CountMeIn);
        Assert.False(h.Vm.ShareServerFirsts);
    }
}
