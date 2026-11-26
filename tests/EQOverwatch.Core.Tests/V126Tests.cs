using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V126Tests
{
    [Fact]
    public void The_layout_is_saved_and_read_back()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UiLayoutStore();
            store.Load(path);
            store.Set("split:Main/Encounter DPS:0:c1", "300|*1");
            store.Set("tabs:Main:hidden", "Maps");
            store.Set("tabs:Main:hidden", null);

            var again = new UiLayoutStore();
            again.Load(path);
            Assert.Equal("300|*1", again.Get("split:Main/Encounter DPS:0:c1"));
            Assert.Null(again.Get("tabs:Main:hidden"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Presets_hold_a_whole_layout_and_reset_keeps_them()
    {
        var store = new UiLayoutStore();
        int replaced = 0;
        store.Replaced += () => replaced++;
        store.Set("zoom:Loot", "1.2");
        store.SavePreset("Raid");
        store.Set("zoom:Loot", "0.8");
        store.Set("rows", "compact");

        Assert.True(store.ApplyPreset("raid"));
        Assert.Equal("1.2", store.Get("zoom:Loot"));
        Assert.Null(store.Get("rows"));

        store.Reset();
        Assert.Null(store.Get("zoom:Loot"));
        Assert.Equal(new[] { "Raid" }, store.PresetNames);
        Assert.Equal(2, replaced);

        store.DeletePreset("Raid");
        Assert.Empty(store.PresetNames);
        Assert.False(store.ApplyPreset("Raid"));
    }
}
