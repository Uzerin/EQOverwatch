using EQCompanion.Core.Parsing;
using Xunit;
using static EQCompanion.Core.Tests.TestData;

namespace EQCompanion.Core.Tests;

public class LogLineFormatTests
{
    [Fact]
    public void Parses_the_26_character_timestamp_wrapper()
    {
        Assert.True(LogLineFormat.TryParseTimestamp(Line("You slash a frost giant for 142 points of damage."), out DateTime ts));
        Assert.Equal(new DateTime(2026, 10, 21, 11, 45, 1), ts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Wed Oct 21 11:45:01 2026")]                       // too short
    [InlineData("Wed Oct 21 11:45:01 2026] no opening bracket..")]
    [InlineData("[Wed Oct 21 25:45:01 2026] impossible hour")]
    [InlineData("[Mon Oct 21 11:45:01 2026] weekday does not match date")]
    public void Rejects_malformed_wrappers(string line)
    {
        Assert.False(LogLineFormat.TryParseTimestamp(line, out _));
    }

    [Fact]
    public void Message_is_everything_after_the_wrapper()
    {
        string line = Line("Your protective skin fades.");
        Assert.Equal("Your protective skin fades.", LogLineFormat.GetMessage(line).ToString());
        Assert.True(LogLineFormat.GetMessage("[Wed Oct 21 11:45:01 2026]").IsEmpty);
    }

    [Fact]
    public void Extracts_character_and_server_from_the_log_file_name()
    {
        Assert.True(LogFileName.TryParse(@"C:\EQ\Logs\eqlog_Stackprime_Frostreaver.txt", out string c, out string s));
        Assert.Equal("Stackprime", c);
        Assert.Equal("Frostreaver", s);

        Assert.False(LogFileName.TryParse("notes.txt", out _, out _));
        Assert.Equal("notes", LogFileName.GetCharacterName("notes.txt"));
    }
}
