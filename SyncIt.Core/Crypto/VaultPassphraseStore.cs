using System.Security.Cryptography;
using System.Text;
using SyncIt.Core.Models;

namespace SyncIt.Core.Crypto;

/// <summary>Keeps an encrypted folder's passphrase on this PC, protected with Windows DPAPI (current user).</summary>
public static class VaultPassphraseStore
{
    private static readonly byte[] Entropy = "SyncIt.Vault.v1"u8.ToArray();

    public static string PathFor(string credentialDir, string pairId) =>
        Path.Combine(credentialDir, $"vault-{string.Concat(pairId.Where(char.IsLetterOrDigit))}.dat");

    public static void Save(string credentialDir, string pairId, string passphrase)
    {
        Directory.CreateDirectory(credentialDir);
        File.WriteAllBytes(PathFor(credentialDir, pairId),
            ProtectedData.Protect(Encoding.UTF8.GetBytes(passphrase), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string? Load(string credentialDir, string pairId)
    {
        var p = PathFor(credentialDir, pairId);
        if (!File.Exists(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public static void Delete(string credentialDir, string pairId)
    {
        try { File.Delete(PathFor(credentialDir, pairId)); } catch { }
    }

    public static VaultCrypto? Open(string credentialDir, SyncPair pair)
    {
        if (pair.EncryptionSalt is null || pair.EncryptionVerifier is null) return null;
        var pass = Load(credentialDir, pair.Id);
        if (pass is null) return null;
        try { return VaultCrypto.Open(pass, new VaultCrypto.VaultParams(pair.EncryptionSalt, pair.EncryptionVerifier, pair.EncryptionIterations)); }
        catch (CryptographicException) { return null; }
    }
}
