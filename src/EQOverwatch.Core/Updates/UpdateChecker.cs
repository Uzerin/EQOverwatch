using System.Net;
using System.Text.Json;

namespace EQOverwatch.Core.Updates;

public enum UpdateCheckStatus
{
    /// <summary>The newest published release is not newer than this version.</summary>
    UpToDate,

    /// <summary>A newer release has been published.</summary>
    UpdateAvailable,

    /// <summary>The repository has no published release yet.</summary>
    NoReleases,

    /// <summary>The check could not be completed (offline, rate limited, unexpected reply, ...).</summary>
    Failed,
}

/// <param name="LatestVersion">"v1.0.12": the newest release's version (empty when unknown).</param>
/// <param name="Url">The release page to send the user to.</param>
/// <param name="Notes">The start of the release notes (may be empty).</param>
/// <param name="Assets">The release's zip and its SHA-256 file, when the release carries both; null when it cannot be installed from the app.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status, string LatestVersion = "", string Url = "", string Notes = "", string Error = "", UpdateAssets? Assets = null);

/// <summary>The files an in-app install needs: the zip holding <c>EQOverwatch.exe</c> and the file with the zip's SHA-256 hash.</summary>
public sealed record UpdateAssets(string ZipName, string ZipUrl, string HashUrl);

public interface IUpdateChecker
{
    /// <summary>Compares <paramref name="currentVersion"/> ("v1.0.11") with the newest published release. Never throws for network trouble.</summary>
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
}

public static class VersionText
{
    /// <summary>"v1.0.12", "1.0.12" and "v1.0.12-beta.1" all parse (anything after a '-' or '+' is ignored).</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        int cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        if (!Version.TryParse(trimmed, out Version? parsed) || parsed.Major < 0 || parsed.Minor < 0)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>"v1.0.12".</summary>
    public static string Format(Version version) => $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}

/// <summary>
/// Asks GitHub for the repository's latest published release (<c>/releases/latest</c>, which ignores drafts and pre-releases) and
/// says whether it is newer than the running version. Works for a public repository without any sign-in.
/// </summary>
public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private const int MaxNotesLength = 600;

    private readonly HttpClient _client;
    private readonly string _repository;

    public GitHubUpdateChecker(HttpClient client, string repository = AppInfo.Repository)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _repository = repository;
    }

    /// <summary>The release's win-x64 zip and the "&lt;zip&gt;.sha256" file next to it, or null when either is missing.</summary>
    internal static UpdateAssets? FindAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? link = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(link))
            {
                urls[name] = link;
            }
        }

        foreach ((string name, string link) in urls)
        {
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) && urls.TryGetValue(name + ".sha256", out string? hashLink))
            {
                return new UpdateAssets(name, link, hashLink);
            }
        }

        return null;
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!VersionText.TryParse(currentVersion, out Version current))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"Cannot read this app's version \"{currentVersion}\".");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"{AppInfo.SettingsFolderName}/{current.Major}.{current.Minor}.{current.Build}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            if (!VersionText.TryParse(tag, out Version latest))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"The newest release's tag \"{tag}\" is not a version number.");
            }

            string url = root.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
            if (url.Length == 0)
            {
                url = AppInfo.ReleasesUrl;
            }

            string notes = root.TryGetProperty("body", out JsonElement bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (notes.Length > MaxNotesLength)
            {
                notes = notes[..MaxNotesLength].TrimEnd() + "…";
            }

            return latest > current
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, VersionText.Format(latest), url, notes, Assets: FindAssets(root))
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, VersionText.Format(latest), url, notes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex is OperationCanceledException ? "The request timed out." : ex.Message);
        }
    }
}
