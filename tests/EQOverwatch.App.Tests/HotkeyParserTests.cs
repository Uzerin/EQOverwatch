using EQOverwatch.App.Interop;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class HotkeyParserTests
{
    [Fact]
    public void A_modifier_plus_a_key_is_read_into_flags_and_a_virtual_key_code()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+F9", out uint modifiers, out uint key));
        Assert.Equal(HotkeyParser.ModControl | HotkeyParser.ModAlt, modifiers);
        Assert.Equal(0x78u, key); // VK_F9

        Assert.True(HotkeyParser.TryParse("  ctrl + shift + c ", out modifiers, out key));
        Assert.Equal(HotkeyParser.ModControl | HotkeyParser.ModShift, modifiers);
        Assert.Equal(0x43u, key); // 'C'

        Assert.True(HotkeyParser.TryParse("Alt+5", out modifiers, out key));
        Assert.Equal(HotkeyParser.ModAlt, modifiers);
        Assert.Equal(0x35u, key); // '5'
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F9")] // a hotkey needs Ctrl, Alt or Shift
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+F9+F10")]
    [InlineData("Ctrl+99")]
    [InlineData("Ctrl+NotAKey")]
    public void Anything_else_is_refused(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _, out _));
    }
}
