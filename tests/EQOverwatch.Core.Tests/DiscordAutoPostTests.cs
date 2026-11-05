using System.Net;
using System.Text;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class DiscordAutoPostTests
{
    private const string Hook = "https://discord.com/api/webhooks/123456/abcdef";
    private static readonly DateTime Now = new(2026, 11, 2, 21, 0, 0);

    private static EncounterSnapshot Fight(string boss = "a Praetorian Pius", double seconds = 120, int players = 12, bool killed = true,
        bool active = false, DateTime? end = null, int deaths = 0, int id = 1)
    {
        DateTime finish = end ?? Now.AddSeconds(-10);
        var people = Enumerable.Range(1, players)
            .Select(i => new ParticipantSnapshot($"P{i}", 1000, 5, 10, 10, 100, 5, 0, 1.0))
            .ToList();
        var deathLog = Enumerable.Range(1, deaths).Select(i => new DeathSnapshot(finish, $"P{i}", "a hit")).ToList();
        return new EncounterSnapshot(id, boss, finish.AddSeconds(-seconds), finish, seconds, 1000L * players, active, people, null, null, null, deathLog, killed);
    }

    private static DiscordAutoPostSelector Selector(bool wipes = false) =>
        new() { Enabled = true, PostWipes = wipes, MinSeconds = 30, MinPlayers = 6 };

    [Fact]
    public void A_fresh_kill_is_posted_once()
    {
        DiscordAutoPostSelector selector = Selector();
        EncounterSnapshot kill = Fight();

        Assert.Single(selector.Select(new[] { kill }, Now));
        Assert.Empty(selector.Select(new[] { kill }, Now.AddSeconds(5))); // already seen
    }

    [Fact]
    public void Nothing_is_posted_while_it_is_off_and_turning_it_on_does_not_send_what_came_before()
    {
        var selector = new DiscordAutoPostSelector { Enabled = false, MinSeconds = 30, MinPlayers = 6 };
        EncounterSnapshot kill = Fight();

        Assert.Empty(selector.Select(new[] { kill }, Now));
        selector.Enabled = true;
        Assert.Empty(selector.Select(new[] { kill }, Now)); // seen while off
        Assert.Single(selector.Select(new[] { Fight(id: 2, end: Now.AddSeconds(-5), boss: "a gnoll") }, Now));
    }

    [Fact]
    public void Old_fights_short_fights_small_fights_and_unfinished_fights_are_skipped()
    {
        DiscordAutoPostSelector selector = Selector();

        Assert.Empty(selector.Select(new[] { Fight(end: Now.AddMinutes(-10)) }, Now));       // read from an old log
        Assert.Empty(selector.Select(new[] { Fight(boss: "a", seconds: 10) }, Now));          // too short
        Assert.Empty(selector.Select(new[] { Fight(boss: "b", players: 3) }, Now));           // too few players
        Assert.Empty(selector.Select(new[] { Fight(boss: "c", active: true) }, Now));         // still going
    }

    [Fact]
    public void Wipes_are_posted_only_when_asked_for()
    {
        EncounterSnapshot wipe = Fight(killed: false);

        Assert.Empty(Selector(wipes: false).Select(new[] { wipe }, Now));
        Assert.Single(Selector(wipes: true).Select(new[] { wipe }, Now));
    }

    [Fact]
    public void The_message_says_what_happened()
    {
        Assert.Equal("**a Praetorian Pius** killed in 2:00 - 100 raid DPS, 12 players, 0 deaths",
            DiscordAutoPostSelector.BuildMessage(Fight()));
        Assert.Equal("**a gnoll** not killed after 1:05 - 200 raid DPS, 13 players, 1 death",
            DiscordAutoPostSelector.BuildMessage(Fight("a gnoll", 65, 13, killed: false, deaths: 1)));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/abc", true)]
    [InlineData("http://discord.com/api/webhooks/1/abc", false)]
    [InlineData("https://example.com/api/webhooks/1/abc", false)]
    [InlineData("https://discord.com/channels/1/2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_discord_webhook_addresses_are_accepted(string? url, bool expected) =>
        Assert.Equal(expected, DiscordAutoPostSelector.IsWebhookUrl(url));

    [Fact]
    public void The_limits_are_kept_in_range()
    {
        var selector = new DiscordAutoPostSelector { MinSeconds = -5, MinPlayers = 0 };
        Assert.Equal(0, selector.MinSeconds);
        Assert.Equal(1, selector.MinPlayers);
        selector.MinSeconds = 99999;
        selector.MinPlayers = 999;
        Assert.Equal(DiscordAutoPostSelector.MaxMinSeconds, selector.MinSeconds);
        Assert.Equal(DiscordAutoPostSelector.MaxMinPlayers, selector.MinPlayers);
    }

    // ───────────────────────── the webhook poster ─────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_post_with_a_picture_is_sent_as_a_multipart_form()
    {
        var handler = new FakeHandler(Reply(HttpStatusCode.NoContent));
        var poster = new DiscordWebhookPoster(new HttpClient(handler));

        string? error = await poster.PostAsync(Hook, "hello", new byte[] { 1, 2, 3 }, "fight.png");

        Assert.Null(error);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Hook, request.RequestUri!.ToString());
        Assert.IsType<MultipartFormDataContent>(request.Content);
    }

    [Fact]
    public async Task A_text_only_post_is_sent_as_json()
    {
        var handler = new FakeHandler(Reply(HttpStatusCode.NoContent));
        var poster = new DiscordWebhookPoster(new HttpClient(handler));

        Assert.Null(await poster.PostAsync(Hook, "test", null, "x.png"));
        Assert.Equal("application/json", Assert.Single(handler.Requests).Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_once_and_tried_again()
    {
        var handler = new FakeHandler(Reply((HttpStatusCode)429, "{\"retry_after\":0.01}"), Reply(HttpStatusCode.NoContent));
        var poster = new DiscordWebhookPoster(new HttpClient(handler));

        Assert.Null(await poster.PostAsync(Hook, "hello", new byte[] { 1 }, "f.png"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_deleted_webhook_and_a_bad_address_give_a_reason_instead_of_throwing()
    {
        var handler = new FakeHandler(Reply(HttpStatusCode.NotFound));
        var poster = new DiscordWebhookPoster(new HttpClient(handler));

        Assert.Contains("webhook", (await poster.PostAsync(Hook, "x", null, "f.png"))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not valid", (await poster.PostAsync("https://example.com/x", "x", null, "f.png"))!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests); // the bad address never reached the network
    }
}
