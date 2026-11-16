using System.Security.Cryptography;
using System.Text;

namespace AmazingWR.App;

/// <summary>Wraps values in Windows DPAPI (current user) so tokens and saved passwords are not stored as plain text.</summary>
internal static class Secrets
{
    public static string Protect(string plain) => string.IsNullOrEmpty(plain)
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) { Log.Write("Could not decrypt a stored secret (it was probably saved by another Windows account)", ex); return ""; }
    }
}
