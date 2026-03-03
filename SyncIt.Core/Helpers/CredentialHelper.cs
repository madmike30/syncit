using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Secure credential storage using:
///  - Windows Credential Manager for clientId/clientSecret
///  - DPAPI (Data Protection API) for the OAuth token file
///
/// Neither the Google OAuth credentials nor the token file are ever stored in plaintext.
/// DPAPI encryption is scoped to the current Windows user account — decryption requires
/// the same user session, making the token useless to other users or on other machines.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialHelper
{
    private const string CredentialTarget = "SyncIt/GoogleOAuth";

    // -------------------------------------------------------------------------
    // Windows Credential Manager (clientId + clientSecret)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Save Google OAuth client credentials to Windows Credential Manager.
    /// </summary>
    public static void SaveClientCredentials(string clientId, string clientSecret)
    {
        // Pack as "clientId|clientSecret" in the credential's user/password fields
        var cred = new CREDENTIAL
        {
            Type           = CredentialType.Generic,
            TargetName     = CredentialTarget,
            UserName       = clientId,
            CredentialBlob = Marshal.StringToHGlobalUni(clientSecret),
            CredentialBlobSize = (uint)(clientSecret.Length * 2),
            Persist        = CredentialPersist.LocalMachine,
        };

        try
        {
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    /// <summary>
    /// Load Google OAuth client credentials from Windows Credential Manager.
    /// Returns (clientId, clientSecret) or throws if not found.
    /// </summary>
    public static (string ClientId, string ClientSecret) LoadClientCredentials()
    {
        if (!CredRead(CredentialTarget, CredentialType.Generic, 0, out var credPtr))
            throw new InvalidOperationException(
                "Google OAuth credentials not found in Windows Credential Manager. " +
                "Run the Tray app first to set up your credentials.");

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var clientId     = cred.UserName ?? string.Empty;
            var clientSecret = cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2))
                : string.Empty;
            return (clientId, clientSecret ?? string.Empty);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>Returns true if credentials exist in Credential Manager.</summary>
    public static bool CredentialsExist() =>
        CredRead(CredentialTarget, CredentialType.Generic, 0, out var ptr) && ptr != IntPtr.Zero;

    // -------------------------------------------------------------------------
    // DPAPI — OAuth token encryption/decryption
    // -------------------------------------------------------------------------

    /// <summary>
    /// Load OAuth token JSON from a DPAPI-encrypted binary file.
    /// Returns the decrypted JSON string, or null if the file doesn't exist.
    /// </summary>
    public static string? LoadTokenJson(string tokenFilePath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(tokenFilePath);
        if (!File.Exists(expanded)) return null;

        var encrypted = File.ReadAllBytes(expanded);
        var decrypted = ProtectedData.Unprotect(
            encrypted,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Save OAuth token JSON to a DPAPI-encrypted binary file.
    /// </summary>
    public static void SaveTokenJson(string tokenFilePath, string tokenJson)
    {
        var expanded = Environment.ExpandEnvironmentVariables(tokenFilePath);
        PathHelper.EnsureDirectory(Path.GetDirectoryName(expanded)!);

        var bytes     = Encoding.UTF8.GetBytes(tokenJson);
        var encrypted = ProtectedData.Protect(
            bytes,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);

        File.WriteAllBytes(expanded, encrypted);

        // Restrict file to owner only: strip inherited ACEs, grant current user Full Control.
        // SetAccessRuleProtection(true, false) removes ALL inherited entries — we must
        // add an explicit rule first, otherwise the file becomes unreadable by everyone.
        var fi      = new FileInfo(expanded);
        var acl     = fi.GetAccessControl();
        var owner   = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            owner,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        fi.SetAccessControl(acl);
    }

    /// <summary>Returns true if a token file exists at the given path.</summary>
    public static bool TokenExists(string tokenFilePath) =>
        File.Exists(Environment.ExpandEnvironmentVariables(tokenFilePath));

    // -------------------------------------------------------------------------
    // Win32 P/Invoke declarations
    // -------------------------------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, CredentialType type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    private enum CredentialType : uint { Generic = 1 }
    private enum CredentialPersist : uint { LocalMachine = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint              Flags;
        public CredentialType    Type;
        public string?           TargetName;
        public string?           Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint              CredentialBlobSize;
        public IntPtr            CredentialBlob;
        public CredentialPersist Persist;
        public uint              AttributeCount;
        public IntPtr            Attributes;
        public string?           TargetAlias;
        public string?           UserName;
    }
}
