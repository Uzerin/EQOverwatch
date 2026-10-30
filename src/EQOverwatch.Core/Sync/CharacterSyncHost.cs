using System.Net;
using System.Net.Sockets;
using System.Text;
using EQOverwatch.Core.Characters;

namespace EQOverwatch.Core.Sync;

/// <summary>
/// The main PC's side of the multi-PC sync: listens on the home network for other PCs' <c>/outputfile</c> files, checks they were
/// sent with the right pairing code, and saves each one under <c>directory\PCname\</c> with its original write time so the normal
/// character-data reader can take it in. A second small listener answers "is there a host?" so other PCs can find this one.
/// </summary>
public sealed class CharacterSyncHost : IDisposable
{
    private const int MaxFileChars = 4_000_000;
    private const int MaxFilesPerBundle = 200;

    private readonly string _directory;
    private readonly Func<string> _code;
    private readonly int _tcpPort;
    private readonly int _discoveryPort;
    private CancellationTokenSource? _cts;
    private TcpListener? _tcp;
    private UdpClient? _udp;

    public CharacterSyncHost(string directory, Func<string> code, int tcpPort = SyncProtocol.TcpPort, int discoveryPort = SyncProtocol.DiscoveryPort)
    {
        _directory = directory;
        _code = code;
        _tcpPort = tcpPort;
        _discoveryPort = discoveryPort;
    }

    /// <summary>The port actually listening (differs from the one asked for only when 0 was asked for, as the tests do).</summary>
    public int Port => (_tcp?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>Raised on a background thread with the sending PC's name and the paths of the files that were saved.</summary>
    public event Action<string, IReadOnlyList<string>>? Received;

    /// <summary>Raised with a short message for the Settings tab.</summary>
    public event Action<string>? StatusChanged;

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpListener(IPAddress.Any, _tcpPort);
            _tcp.Start();
        }
        catch (SocketException ex)
        {
            _tcp = null;
            StatusChanged?.Invoke($"Could not start listening on port {_tcpPort}: {ex.Message}");
            cts.Dispose();
            return;
        }

        _cts = cts;
        _ = Task.Run(() => AcceptLoopAsync(_tcp, cts.Token));

        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort));
            UdpClient udp = _udp;
            _ = Task.Run(() => DiscoveryLoopAsync(udp, cts.Token));
        }
        catch (SocketException)
        {
            _udp = null; // other PCs can still be pointed at this PC's address by hand
        }

        StatusChanged?.Invoke("Waiting for other PCs.");
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

        try
        {
            _tcp?.Stop();
        }
        catch (SocketException)
        {
        }

        _tcp = null;
        _udp?.Dispose();
        _udp = null;
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task DiscoveryLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult request = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(request.Buffer) == SyncProtocol.DiscoveryRequest)
                {
                    byte[] reply = Encoding.ASCII.GetBytes(SyncProtocol.DiscoveryReplyPrefix + Port);
                    await udp.SendAsync(reply, reply.Length, request.RemoteEndPoint).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // a bad datagram or a network change: keep listening
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                NetworkStream stream = client.GetStream();

                byte[]? frame = await SyncProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                string code = _code();
                if (frame is null || string.IsNullOrEmpty(SyncProtocol.NormalizeCode(code)))
                {
                    return;
                }

                SyncBundle? bundle = SyncProtocol.Unpack(code, frame);
                if (bundle is null)
                {
                    StatusChanged?.Invoke("A PC tried to send data with the wrong pairing code.");
                    return; // closing without a reply tells the sender the code was not accepted
                }

                IReadOnlyList<string> saved = Save(bundle);
                byte[] reply = SyncProtocol.Encrypt(code, Encoding.UTF8.GetBytes("OK"));
                await SyncProtocol.WriteFrameAsync(stream, reply, timeout.Token).ConfigureAwait(false);

                if (saved.Count > 0)
                {
                    StatusChanged?.Invoke($"Received {saved.Count} file{(saved.Count == 1 ? string.Empty : "s")} from {SafePcName(bundle.Pc)} at {DateTime.Now:HH:mm:ss}.");
                    Received?.Invoke(SafePcName(bundle.Pc), saved);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // the other PC went away or was too slow: nothing to do
            }
        }
    }

    private IReadOnlyList<string> Save(SyncBundle bundle)
    {
        var saved = new List<string>();
        string folder = Path.Combine(_directory, SafePcName(bundle.Pc));
        Directory.CreateDirectory(folder);
        foreach (SyncFile file in bundle.Files.Take(MaxFilesPerBundle))
        {
            string name = Path.GetFileName(file.Name ?? string.Empty);
            if (name.Length == 0 || file.Text is null || file.Text.Length > MaxFileChars
                || !CharacterOutputParser.TryParseFileName(name, out _))
            {
                continue; // only /outputfile files are accepted, under names that cannot point anywhere else
            }

            string path = Path.Combine(folder, name);
            File.WriteAllText(path, file.Text);
            File.SetLastWriteTimeUtc(path, file.WrittenUtc.ToUniversalTime());
            saved.Add(path);
        }

        return saved;
    }

    /// <summary>The PC name reduced to letters, digits, spaces, dashes and underscores so it is safe as a folder name.</summary>
    public static string SafePcName(string? name)
    {
        string cleaned = new string((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (cleaned.Length > 32)
        {
            cleaned = cleaned[..32];
        }

        return cleaned.Length == 0 ? "PC" : cleaned;
    }
}
