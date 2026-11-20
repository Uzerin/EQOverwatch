using EQOverwatch.Core.Parsing;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release76Tests
{
    [Fact]
    public void Con_lines_with_an_encounter_lock_tag_are_read()
    {
        Assert.True(ConLineParser.TryParse("A Praetorian Pius scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 62) (Encounter Lock: Stackprime)", out string mob, out int level));
        Assert.Equal(("a Praetorian Pius", 62), (mob, level));
        Assert.True(ConLineParser.TryParse("Xi Xaui scowls at you, ready to attack -- looks kind of dangerous. (Lvl: 51) (Encounter Lock: Myriad)", out mob, out level));
        Assert.Equal(("Xi Xaui", 51), (mob, level));

        Assert.True(ConLineParser.TryParse("Xi Vius scowls at you, ready to attack -- looks kind of dangerous. (Lvl: 48)", out mob, out level));
        Assert.Equal(("Xi Vius", 48), (mob, level));
        Assert.False(ConLineParser.TryParse("Xi Vius scowls at you, ready to attack -- looks kind of dangerous. (Lvl: 48) and more (x)", out _, out _));
        Assert.False(ConLineParser.TryParse("Pecos tells you, 'see you at 8 (Lvl: 62) (ok)'", out _, out _));
    }
}
