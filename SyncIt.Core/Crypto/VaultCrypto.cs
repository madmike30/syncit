using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SyncIt.Core.Crypto;

/// <summary>
/// EXPERIMENTAL (beta). Client-side encryption for "encrypted folder" pairs.
/// Scheme (versioned, documented in docs/ENCRYPTION.md):
///  • Master key: PBKDF2-HMAC-SHA256(passphrase, 16-byte random salt, 600 000 iterations) → 64 bytes
///    (32 content key-encryption key, 32 name key). Only a verifier (HMAC of a constant) is stored, never the key.
///  • File content: per-file random 32-byte data key, wrapped with AES-256-GCM under the master content key.
///    Data is split into 64 KiB chunks, each AES-256-GCM with nonce = 8-byte random file prefix ‖ 4-byte chunk index,
///    AAD = header ‖ chunk index ‖ final-flag, so chunks cannot be reordered, dropped or truncated undetected.
///  • File/folder names: deterministic AES-SIV-like construction: nonce = HMAC-SHA256(nameKey, name)[..12],
///    AES-GCM(name) → base32. Same name always maps to the same encrypted name (needed for sync), tampering is detected.
/// </summary>
public sealed class VaultCrypto
{
    public const int Iterations = 600_000;
    public const int ChunkSize = 64 * 1024;
    private static readonly byte[] Magic = "SYNCITE1"u8.ToArray();
    private const int TagSize = 16, NonceSize = 12, HeaderSize = 8 + 8 + NonceSize + 32 + TagSize; // magic, prefix, wrap nonce, wrapped key, tag

    private readonly byte[] _contentKey;
    private readonly byte[] _nameKey;

    private VaultCrypto(byte[] contentKey, byte[] nameKey) { _contentKey = contentKey; _nameKey = nameKey; }

    public sealed record VaultParams(string SaltB64, string VerifierB64, int Iterations);

    public static VaultParams CreateParams(string passphrase, out VaultCrypto crypto)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 10)
            throw new ArgumentException("Use a passphrase of at least 10 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        crypto = Derive(passphrase, salt, Iterations);
        return new VaultParams(Convert.ToBase64String(salt), Convert.ToBase64String(crypto.Verifier()), Iterations);
    }

    /// <summary>Opens a vault. Throws <see cref="CryptographicException"/> on a wrong passphrase.</summary>
    public static VaultCrypto Open(string passphrase, VaultParams p)
    {
        var c = Derive(passphrase, Convert.FromBase64String(p.SaltB64), p.Iterations);
        if (!CryptographicOperations.FixedTimeEquals(c.Verifier(), Convert.FromBase64String(p.VerifierB64)))
            throw new CryptographicException("Wrong passphrase.");
        return c;
    }

    private static VaultCrypto Derive(string passphrase, byte[] salt, int iterations)
    {
        var km = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 64);
        return new VaultCrypto(km[..32], km[32..]);
    }

    private byte[] Verifier() => HMACSHA256.HashData(_contentKey, "syncit-vault-verifier-v1"u8);

    // ── content ────────────────────────────────────────────────────────────
    public void Encrypt(Stream input, Stream output)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var prefix = RandomNumberGenerator.GetBytes(8);
        var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrapped = new byte[32]; var wrapTag = new byte[TagSize];
        using (var g = new AesGcm(_contentKey, TagSize)) g.Encrypt(wrapNonce, dataKey, wrapped, wrapTag, Magic);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0); prefix.CopyTo(header, 8); wrapNonce.CopyTo(header, 16); wrapped.CopyTo(header, 28); wrapTag.CopyTo(header, 60);
        output.Write(header);

        using var gcm = new AesGcm(dataKey, TagSize);
        var buf = new byte[ChunkSize]; var next = new byte[ChunkSize];
        int n = ReadFull(input, buf);
        uint index = 0;
        while (true)
        {
            int m = n == ChunkSize ? ReadFull(input, next) : 0;
            bool final = m == 0;
            var ct = new byte[n]; var tag = new byte[TagSize];
            gcm.Encrypt(ChunkNonce(prefix, index), buf.AsSpan(0, n), ct, tag, Aad(header, index, final));
            output.Write(ct); output.Write(tag);
            if (final) break;
            (buf, next) = (next, buf); n = m; index++;
        }
        CryptographicOperations.ZeroMemory(dataKey);
    }

    public void Decrypt(Stream input, Stream output)
    {
        var header = new byte[HeaderSize];
        if (ReadFull(input, header) != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new CryptographicException("Not a SyncIt encrypted file.");
        var dataKey = new byte[32];
        using (var g = new AesGcm(_contentKey, TagSize))
            g.Decrypt(header.AsSpan(16, NonceSize), header.AsSpan(28, 32), header.AsSpan(60, TagSize), dataKey, Magic);
        var prefix = header[8..16];
        using var gcm = new AesGcm(dataKey, TagSize);
        var buf = new byte[ChunkSize + TagSize]; var next = new byte[ChunkSize + TagSize];
        int n = ReadFull(input, buf);
        uint index = 0;
        while (true)
        {
            if (n < TagSize) throw new CryptographicException("Encrypted file is truncated.");
            int m = n == buf.Length ? ReadFull(input, next) : 0;
            bool final = m == 0;
            var pt = new byte[n - TagSize];
            gcm.Decrypt(ChunkNonce(prefix, index), buf.AsSpan(0, n - TagSize), buf.AsSpan(n - TagSize, TagSize), pt, Aad(header, index, final));
            output.Write(pt);
            if (final) break;
            (buf, next) = (next, buf); n = m; index++;
        }
        CryptographicOperations.ZeroMemory(dataKey);
    }

    private static byte[] ChunkNonce(byte[] prefix, uint index)
    {
        var nonce = new byte[NonceSize]; prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index); return nonce;
    }

    private static byte[] Aad(byte[] header, uint index, bool final)
    {
        var aad = new byte[header.Length + 5]; header.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(header.Length), index); aad[^1] = final ? (byte)1 : (byte)0; return aad;
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0, r;
        while (total < buf.Length && (r = s.Read(buf, total, buf.Length - total)) > 0) total += r;
        return total;
    }

    // ── names ──────────────────────────────────────────────────────────────
    public string EncryptName(string name)
    {
        var pt = Encoding.UTF8.GetBytes(name);
        var nonce = HMACSHA256.HashData(_nameKey, pt)[..NonceSize];
        var ct = new byte[pt.Length]; var tag = new byte[TagSize];
        using (var g = new AesGcm(_nameKey, TagSize)) g.Encrypt(nonce, pt, ct, tag);
        return Base32.Encode([.. nonce, .. ct, .. tag]);
    }

    public string DecryptName(string encrypted)
    {
        var raw = Base32.Decode(encrypted);
        if (raw.Length < NonceSize + TagSize) throw new CryptographicException("Not an encrypted name.");
        var pt = new byte[raw.Length - NonceSize - TagSize];
        using (var g = new AesGcm(_nameKey, TagSize))
            g.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize, pt.Length), raw.AsSpan(raw.Length - TagSize), pt);
        var name = Encoding.UTF8.GetString(pt);
        if (!HMACSHA256.HashData(_nameKey, pt).AsSpan(0, NonceSize).SequenceEqual(raw.AsSpan(0, NonceSize)))
            throw new CryptographicException("Name was tampered with.");
        return name;
    }

    public string EncryptPath(string relativePath) =>
        string.Join('/', relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(EncryptName));

    public string DecryptPath(string encryptedPath) =>
        string.Join('/', encryptedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(DecryptName));
}

/// <summary>RFC 4648 base32, lowercase, no padding (safe for Windows and Drive names, case-insensitive).</summary>
public static class Base32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
    public static byte[] Decode(string s)
    {
        var list = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in s.ToLowerInvariant())
        {
            var v = Alphabet.IndexOf(ch);
            if (v < 0) throw new FormatException("Invalid base32.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { list.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return list.ToArray();
    }
}
