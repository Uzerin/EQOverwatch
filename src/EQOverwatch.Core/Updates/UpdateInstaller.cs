using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Updates;

/// <summary>An update could not be downloaded, verified or put in place. The message is fit to show the user.</summary>
public sealed class UpdateInstallException : Exception
{
    public UpdateInstallException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public interface IUpdateInstaller
{
    /// <summary>
    /// Downloads the release zip, checks it against the published SHA-256 and unpacks <c>EQOverwatch.exe</c> into
    /// <paramref name="stagingDirectory"/>. Returns the path of the new exe. Throws <see cref="UpdateInstallException"/> when anything is wrong.
    /// </summary>
    Task<string> PrepareAsync(UpdateAssets assets, string stagingDirectory, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed partial class UpdateInstaller : IUpdateInstaller
{
    public const string ExeName = "EQOverwatch.exe";
    private const long MaxZipBytes = 400L * 1024 * 1024;
    private const int MaxHashBytes = 4096;

    private readonly HttpClient _client;
    private readonly string _repository;

    public UpdateInstaller(HttpClient client, string repository = AppInfo.Repository)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _repository = repository;
    }

    /// <summary>Only files attached to a release of this repository, over HTTPS, are ever downloaded.</summary>
    public bool IsTrustedUrl(string url) =>
        url.StartsWith($"https://github.com/{_repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public async Task<string> PrepareAsync(UpdateAssets assets, string stagingDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (!IsTrustedUrl(assets.ZipUrl) || !IsTrustedUrl(assets.HashUrl))
        {
            throw new UpdateInstallException("The update's download address is not on this app's GitHub releases, so it was not downloaded.");
        }

        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            Directory.CreateDirectory(stagingDirectory);

            progress?.Report("Getting the checksum…");
            string expected = await ReadExpectedHashAsync(assets.HashUrl, cancellationToken).ConfigureAwait(false);

            string zipPath = Path.Combine(stagingDirectory, "update.zip");
            string actual = await DownloadAsync(assets.ZipUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zipPath);
                throw new UpdateInstallException("The download did not match its published SHA-256 checksum, so it was thrown away and nothing was installed.");
            }

            progress?.Report("Unpacking…");
            string exePath = Path.Combine(stagingDirectory, ExeName);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                // Only the exe at the top of the zip, copied by hand: no entry name is ever used as a path.
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExeName, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    throw new UpdateInstallException($"The update zip does not contain {ExeName}.");
                }

                using Stream source = entry.Open();
                using FileStream target = File.Create(exePath);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            TryDelete(zipPath);
            return exePath;
        }
        catch (UpdateInstallException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateInstallException($"The update could not be downloaded: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateInstallException("The download timed out.", ex);
        }
    }

    private async Task<string> ReadExpectedHashAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxHashBytes];
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        string text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

        Match match = HashRegex().Match(text);
        if (!match.Success)
        {
            throw new UpdateInstallException("The release has no readable SHA-256 checksum, so it cannot be installed safely.");
        }

        return match.Value;
    }

    private async Task<string> DownloadAsync(string url, string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? length = response.Content.Headers.ContentLength;
        if (length > MaxZipBytes)
        {
            throw new UpdateInstallException("The update is larger than expected, so it was not downloaded.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = File.Create(path);

        var buffer = new byte[81920];
        long total = 0;
        long lastReported = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxZipBytes)
            {
                throw new UpdateInstallException("The update is larger than expected, so the download was stopped.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            long megabytes = total / (1024 * 1024);
            if (megabytes != lastReported)
            {
                lastReported = megabytes;
                progress?.Report(length is > 0
                    ? $"Downloading… {megabytes} of {length.Value / (1024 * 1024)} MB"
                    : $"Downloading… {megabytes} MB");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{64}", RegexOptions.CultureInvariant)]
    private static partial Regex HashRegex();
}

/// <summary>Puts a new exe where the running one is. Windows lets a running exe be renamed, so the old one moves aside and the new one takes its name.</summary>
public static class UpdateSwapper
{
    public const string BackupSuffix = ".old";
    public const string StagingFolderName = ".update";

    public static string BackupPath(string currentExe) => currentExe + BackupSuffix;

    /// <summary>Moves <paramref name="currentExe"/> to its backup name and <paramref name="newExe"/> into its place. On failure the old exe is put back.</summary>
    public static void Swap(string currentExe, string newExe)
    {
        string backup = BackupPath(currentExe);
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(currentExe, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateInstallException($"The current program file could not be replaced: {ex.Message}", ex);
        }

        try
        {
            File.Move(newExe, currentExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(backup, currentExe, overwrite: true);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                throw new UpdateInstallException(
                    $"The new version could not be put in place and the old one could not be restored. It is saved as {backup}: rename it back to {Path.GetFileName(currentExe)}.", ex);
            }

            throw new UpdateInstallException($"The new version could not be put in place; the old one was kept. {ex.Message}", ex);
        }
    }

    /// <summary>Removes the leftovers of an earlier update (the backup of the old exe and the staging folder). Never throws.</summary>
    public static void CleanUp(string currentExe)
    {
        try
        {
            string backup = BackupPath(currentExe);
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            string staging = Path.Combine(Path.GetDirectoryName(currentExe) ?? string.Empty, StagingFolderName);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leftovers are harmless; try again next time
        }
    }
}
