using System.Net;
using System.Text;
using EQCompanion.Core;
using EQCompanion.Core.Updates;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class UpdateCheckerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (GitHubUpdateChecker Checker, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new GitHubUpdateChecker(new HttpClient(handler)), handler);
    }

    private const string Release =
        "{\"tag_name\":\"v1.0.12\",\"html_url\":\"https://github.com/Uzerin/EQOverwatch/releases/tag/v1.0.12\",\"body\":\"New stuff\"}";

    [Fact]
    public async Task A_newer_release_is_reported_with_its_page_and_notes()
    {
        (GitHubUpdateChecker checker, StubHandler handler) = Make(_ => Json(Release));

        UpdateCheckResult result = await checker.CheckAsync("v1.0.11");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v1.0.12", result.LatestVersion);
        Assert.Equal("https://github.com/Uzerin/EQOverwatch/releases/tag/v1.0.12", result.Url);
        Assert.Equal("New stuff", result.Notes);
        Assert.Equal("https://api.github.com/repos/Uzerin/EQOverwatch/releases/latest", handler.LastRequest!.RequestUri!.ToString());
        Assert.NotEmpty(handler.LastRequest.Headers.UserAgent); // GitHub rejects requests without one
    }

    [Theory]
    [InlineData("v1.0.12", "v1.0.12")]
    [InlineData("v1.0.12", "v1.0.13")] // running something newer than the last release
    [InlineData("v1.0.9", "v1.0.10")]
    public async Task The_same_or_an_older_release_is_up_to_date(string released, string running)
    {
        (GitHubUpdateChecker checker, _) = Make(_ => Json($"{{\"tag_name\":\"{released}\",\"html_url\":\"x\"}}"));

        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(running)).Status);
    }

    [Fact]
    public async Task Versions_are_compared_as_numbers_not_text()
    {
        (GitHubUpdateChecker checker, _) = Make(_ => Json("{\"tag_name\":\"v1.0.10\",\"html_url\":\"x\"}"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await checker.CheckAsync("v1.0.9")).Status);
    }

    [Fact]
    public async Task A_repository_without_releases_is_not_an_error()
    {
        (GitHubUpdateChecker checker, _) = Make(_ => Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));

        Assert.Equal(UpdateCheckStatus.NoReleases, (await checker.CheckAsync("v1.0.11")).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Other_http_errors_fail_quietly(HttpStatusCode status)
    {
        (GitHubUpdateChecker checker, _) = Make(_ => Json("{}", status));

        UpdateCheckResult result = await checker.CheckAsync("v1.0.11");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains(((int)status).ToString(), result.Error);
    }

    [Fact]
    public async Task Garbage_a_non_version_tag_and_network_errors_fail_quietly()
    {
        (GitHubUpdateChecker garbage, _) = Make(_ => Json("<html>not json</html>"));
        Assert.Equal(UpdateCheckStatus.Failed, (await garbage.CheckAsync("v1.0.11")).Status);

        (GitHubUpdateChecker weirdTag, _) = Make(_ => Json("{\"tag_name\":\"nightly\",\"html_url\":\"x\"}"));
        Assert.Equal(UpdateCheckStatus.Failed, (await weirdTag.CheckAsync("v1.0.11")).Status);

        (GitHubUpdateChecker offline, _) = Make(_ => throw new HttpRequestException("No such host is known."));
        UpdateCheckResult result = await offline.CheckAsync("v1.0.11");
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("No such host", result.Error);

        (GitHubUpdateChecker timedOut, _) = Make(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(UpdateCheckStatus.Failed, (await timedOut.CheckAsync("v1.0.11")).Status);
    }

    [Fact]
    public async Task A_missing_page_url_falls_back_to_the_releases_page_and_long_notes_are_trimmed()
    {
        string notes = new('x', 2000);
        (GitHubUpdateChecker checker, _) = Make(_ => Json($"{{\"tag_name\":\"v2.0.0\",\"body\":\"{notes}\"}}"));

        UpdateCheckResult result = await checker.CheckAsync("v1.0.11");

        Assert.Equal(AppInfo.ReleasesUrl, result.Url);
        Assert.True(result.Notes.Length <= 601);
        Assert.EndsWith("…", result.Notes);
    }

    [Fact]
    public async Task Cancelling_the_check_is_passed_on()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var checker = new GitHubUpdateChecker(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync("v1.0.11", cts.Token));
    }

    [Theory]
    [InlineData("v1.0.12", 1, 0, 12)]
    [InlineData("1.0.12", 1, 0, 12)]
    [InlineData("V2.1", 2, 1, 0)]
    [InlineData("v1.0.12-beta.1", 1, 0, 12)]
    [InlineData(" v1.0.12+abc ", 1, 0, 12)]
    public void Version_text_is_read_leniently(string text, int major, int minor, int build)
    {
        Assert.True(VersionText.TryParse(text, out Version version));
        Assert.Equal(new Version(major, minor, build), version);
        Assert.Equal($"v{major}.{minor}.{build}", VersionText.Format(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("v1")]
    [InlineData(null)]
    public void Non_versions_are_rejected(string? text) => Assert.False(VersionText.TryParse(text, out _));
}
