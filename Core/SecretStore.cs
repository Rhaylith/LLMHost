using System.Security.Cryptography;
using System.Text;

namespace LLMHostProject.Core;

/// <summary>
/// Protects small secrets (the Hugging Face token) on disk with the Windows Data Protection API, tied to the current Windows user:
/// the stored text is useless on another account or machine. Values are written as <c>dpapi:&lt;base64&gt;</c>.
/// </summary>
public static class SecretStore
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LLMHost.v1");

    public static bool IsProtected(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The plain text, or null when the value cannot be opened (another user / machine, or damaged).</summary>
    public static string? Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
