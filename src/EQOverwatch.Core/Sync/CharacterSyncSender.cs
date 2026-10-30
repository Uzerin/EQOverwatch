using System.Net;
using System.Net.Sockets;
using System.Text;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Discovery;

namespace EQOverwatch.Core.Sync;

public enum SyncSendResult
{
    Sent,
    Unreachable,
    WrongCode,
}

/// <summary>
/// An additional PC's side of the sync: finds the main PC on the home network, and sends it the <c>/outputfile</c> files in this
/// PC's EverQuest folder whenever they are new or changed.
/// </summary>
public sealed class CharacterSyncSender : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _eqFolder;
    private readonly Func<string> _code;
    private readonly Func<string> _pcName;
    private readonly Func<string> _manualHost;
    private readonly Dictionary<string, DateTime> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private CancellationTokenSource? _cts;
    private string? _foundHost;

    public CharacterSyncSender(Func<string?> eqFolder, Func<string> code, Func<string> pcName, Func<string> manualHost)
    {
        _eqFolder = eqFolder;
        _code = code;
        _pcName = pcName;
        _manualHost = manualHost;
    }

    /// <summary>Raised from a background thread with a short message for the Settings tab.</summary>
    public event Action<string>? StatusChanged;

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts = _cts;
        _cts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _busy.Dispose();
    }

    /// <summary>Sends every output file again, now (the "Send now" button).</summary>
    public async Task SendNowAsync(CancellationToken ct = default)
    {
        await CheckAsync(force: true, ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(force: false, ct).ConfigureAwait(false);
                await Task.Delay(CheckEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAsync(bool force, CancellationToken ct)
    {
        if (force)
        {
            await _busy.WaitAsync(ct).ConfigureAwait(false);
        }
        else if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (force)
            {
                _sent.Clear();
                _foundHost = null;
            }

            string code = _code();
            if (SyncProtocol.NormalizeCode(code).Length == 0)
            {
                StatusChanged?.Invoke("Type the pairing code shown on the main PC.");
                return;
            }

            List<SyncFile> changed = CollectChanged(_eqFolder(), _sent);
            if (changed.Count == 0)
            {
                if (_sent.Count == 0)
                {
                    StatusChanged?.Invoke("No /outputfile files found in the EverQuest folder yet.");
                }

                return;
            }

            string? host = await ResolveHostAsync(ct).ConfigureAwait(false);
            if (host is null)
            {
                StatusChanged?.Invoke("Could not find the main PC on this network. Check that it is running Overwatch in Host mode, or type its address.");
                return;
            }

            SyncSendResult result = await SendAsync(host, code, new SyncBundle(_pcName(), changed), ct).ConfigureAwait(false);
            switch (result)
            {
                case SyncSendResult.Sent:
                    foreach (SyncFile file in changed)
                    {
                        _sent[file.Name] = file.WrittenUtc;
                    }

                    StatusChanged?.Invoke($"Sent {changed.Count} file{(changed.Count == 1 ? string.Empty : "s")} to {host} at {DateTime.Now:HH:mm:ss}.");
                    break;
                case SyncSendResult.WrongCode:
                    StatusChanged?.Invoke("The main PC did not accept the pairing code. Check it matches the one shown there.");
                    break;
                default:
                    _foundHost = null;
                    StatusChanged?.Invoke($"Could not reach {host}. It will try again.");
                    break;
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<string?> ResolveHostAsync(CancellationToken ct)
    {
        string manual = _manualHost().Trim();
        if (manual.Length > 0)
        {
            return manual;
        }

        if (_foundHost is not null)
        {
            return _foundHost;
        }

        IPAddress? found = await DiscoverHostAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        _foundHost = found?.ToString();
        return _foundHost;
    }

    /// <summary>Asks the home network "is a Host there?" and returns the address of the first PC that answers.</summary>
    public static async Task<IPAddress?> DiscoverHostAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            byte[] request = Encoding.ASCII.GetBytes(SyncProtocol.DiscoveryRequest);
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, SyncProtocol.DiscoveryPort)).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            while (true)
            {
                UdpReceiveResult reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(reply.Buffer).StartsWith(SyncProtocol.DiscoveryReplyPrefix, StringComparison.Ordinal))
                {
                    return reply.RemoteEndPoint.Address;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Sends one bundle. A host that closes without answering did not accept the code.</summary>
    public static async Task<SyncSendResult> SendAsync(string host, string code, SyncBundle bundle, CancellationToken ct, int port = SyncProtocol.TcpPort)
    {
        try
        {
            using var client = new TcpClient();
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);

            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exchange.CancelAfter(TimeSpan.FromSeconds(20));
            NetworkStream stream = client.GetStream();
            await SyncProtocol.WriteFrameAsync(stream, SyncProtocol.Pack(code, bundle), exchange.Token).ConfigureAwait(false);

            byte[]? reply = await SyncProtocol.ReadFrameAsync(stream, exchange.Token).ConfigureAwait(false);
            byte[]? plain = reply is null ? null : SyncProtocol.Decrypt(code, reply);
            return plain is not null && Encoding.UTF8.GetString(plain) == "OK" ? SyncSendResult.Sent : SyncSendResult.WrongCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            return SyncSendResult.Unreachable;
        }
    }

    /// <summary>The output files in the EverQuest folder that are new or have changed since they were last sent.</summary>
    public static List<SyncFile> CollectChanged(string? eqFolder, IReadOnlyDictionary<string, DateTime> alreadySent)
    {
        var files = new List<SyncFile>();
        string folder = EqDirectoryScanner.Normalize(eqFolder);
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return files;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*_*-*"))
            {
                string name = Path.GetFileName(path);
                if (!CharacterOutputParser.TryParseFileName(name, out _))
                {
                    continue;
                }

                DateTime written = File.GetLastWriteTimeUtc(path);
                if (alreadySent.TryGetValue(name, out DateTime sent) && sent >= written)
                {
                    continue;
                }

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    files.Add(new SyncFile(name, written, reader.ReadToEnd()));
                }
                catch (IOException)
                {
                    // the game still has it open: it is picked up on the next check
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return files;
    }
}
