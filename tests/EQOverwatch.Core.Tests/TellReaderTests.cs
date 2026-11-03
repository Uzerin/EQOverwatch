using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class TellReaderTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 21, 0, 29, DateTimeKind.Utc);

    private static (TellReaderService Reader, List<TriggerFiredEvent> Spoken) Make()
    {
        var engine = new TriggerEngineService();
        var spoken = new List<TriggerFiredEvent>();
        engine.TriggerFired += spoken.Add;
        return (new TellReaderService(engine), spoken);
    }

    [Fact]
    public void A_tell_is_read_as_sender_tells_character_then_the_message()
    {
        (TellReaderService reader, List<TriggerFiredEvent> spoken) = Make();

        reader.OnLine("Stackprimeiiv", "Catiua tells you, 'I SEE YOUUUUUU'", Now);

        TriggerFiredEvent e = Assert.Single(spoken);
        Assert.Equal("Catiua tells Stackprimeiiv: I SEE YOUUUUUU", e.AudioText);
        Assert.Equal("Stackprimeiiv", e.CharacterName);
        Assert.True((e.Actions & TriggerActions.Audio) != 0);
    }

    [Fact]
    public void Each_watched_character_is_named_in_the_tells_it_receives()
    {
        (TellReaderService reader, List<TriggerFiredEvent> spoken) = Make();

        reader.OnLine("Pecos", "Catiua tells you, 'hello'", Now);
        reader.OnLine("Oodles", "Catiua tells you, 'hello'", Now); // the same words to another character are a different tell

        Assert.Equal(new[] { "Catiua tells Pecos: hello", "Catiua tells Oodles: hello" }, spoken.Select(s => s.AudioText));
    }

    [Theory]
    [InlineData("You told Catiua, 'I SEE YOU'")]
    [InlineData("Catiua tells the guild, 'I SEE YOU'")]
    [InlineData("Catiua tells the raid, 'I SEE YOU'")]
    [InlineData("Catiua says, 'I SEE YOU'")]
    [InlineData("a guard tells you, 'move along'")]
    [InlineData("Catiua tells you, ''")]
    [InlineData("Catiua tells you, 'unfinished")]
    [InlineData("You slash a frost giant for 100 points of damage.")]
    [InlineData("")]
    public void Lines_that_are_not_tells_to_you_are_not_read(string message)
    {
        (TellReaderService reader, List<TriggerFiredEvent> spoken) = Make();

        reader.OnLine("Stackprimeiiv", message, Now);

        Assert.Empty(spoken);
    }

    [Fact]
    public void Turning_it_off_silences_tells()
    {
        (TellReaderService reader, List<TriggerFiredEvent> spoken) = Make();
        reader.IsEnabled = false;

        reader.OnLine("Stackprimeiiv", "Catiua tells you, 'hello'", Now);
        Assert.Empty(spoken);

        reader.IsEnabled = true;
        reader.OnLine("Stackprimeiiv", "Catiua tells you, 'hello'", Now);
        Assert.Single(spoken);
    }

    [Fact]
    public void A_long_message_is_cut_off_and_an_immediate_repeat_is_skipped()
    {
        (TellReaderService reader, List<TriggerFiredEvent> spoken) = Make();
        string longText = new('x', 500);

        reader.OnLine("Pecos", $"Catiua tells you, '{longText}'", Now);
        reader.OnLine("Pecos", $"Catiua tells you, '{longText}'", Now.AddSeconds(1)); // repeated at once
        reader.OnLine("Pecos", $"Catiua tells you, '{longText}'", Now.AddSeconds(10)); // later: a new tell

        Assert.Equal(2, spoken.Count);
        Assert.Equal("Catiua tells Pecos: ".Length + TellReaderService.MaxSpokenLength, spoken[0].AudioText!.Length);
    }

    [Fact]
    public void Reading_tells_aloud_is_on_by_default()
    {
        Assert.True(new AppSettings().ReadTellsAloud);
    }
}
