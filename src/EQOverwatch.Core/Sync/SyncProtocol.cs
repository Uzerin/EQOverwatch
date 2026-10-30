using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQOverwatch.Core.Sync;

/// <summary>One <c>/outputfile</c> file sent from another PC: its name, when it was written, and its text.</summary>
public sealed record SyncFile(string Name, DateTime WrittenUtc, string Text);

/// <summary>What a PC sends to the main PC: its name and the files that changed.</summary>
public sealed record SyncBundle(string Pc, List<SyncFile> Files);

/// <summary>
/// The wire format of the multi-PC character sync. Every message is encrypted with a key made from the pairing code, so a PC that
/// does not know the code can neither send data nor read any. A message is <c>salt(16) nonce(12) tag(16) ciphertext</c>, sent
/// as a length-prefixed frame over TCP.
/// </summary>
public static class SyncProtocol
{
    public const int TcpPort = 47621;
    public const int DiscoveryPort = 47622;
    public const string DiscoveryRequest = "EQOVERWATCH?";
    public const string DiscoveryReplyPrefix = "EQOVERWATCH!";
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 50_000;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O or 1/I to mistype

    /// <summary>A new random pairing code such as "K7QMT-4TXD2".</summary>
    public static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length]; // 256 is a multiple of 32, so there is no bias
        }

        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    /// <summary>The code in capitals without dashes or spaces, so "k7qmt 4txd2" and "K7QMT-4TXD2" are the same code.</summary>
    public static string NormalizeCode(string? code) =>
        new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static byte[] Encrypt(string code, byte[] plain)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(DeriveKey(code, salt), TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] result = new byte[SaltSize + NonceSize + TagSize + cipher.Length];
        salt.CopyTo(result, 0);
        nonce.CopyTo(result, SaltSize);
        tag.CopyTo(result, SaltSize + NonceSize);
        cipher.CopyTo(result, SaltSize + NonceSize + TagSize);
        return result;
    }

    /// <summary>The decrypted bytes, or null when the code is wrong or the message was changed.</summary>
    public static byte[]? Decrypt(string code, byte[] message)
    {
        if (message.Length < SaltSize + NonceSize + TagSize)
        {
            return null;
        }

        try
        {
            byte[] salt = message[..SaltSize];
            byte[] nonce = message[SaltSize..(SaltSize + NonceSize)];
            byte[] tag = message[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
            byte[] cipher = message[(SaltSize + NonceSize + TagSize)..];
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(DeriveKey(code, salt), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static byte[] Pack(string code, SyncBundle bundle) =>
        Encrypt(code, JsonSerializer.SerializeToUtf8Bytes(bundle));

    public static SyncBundle? Unpack(string code, byte[] message)
    {
        byte[]? plain = Decrypt(code, message);
        if (plain is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncBundle>(plain);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns null when the other side closed or the size is not acceptable.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            return null;
        }

        byte[] payload = new byte[length];
        return await ReadExactAsync(stream, payload, ct).ConfigureAwait(false) ? payload : null;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static byte[] DeriveKey(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(NormalizeCode(code)), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
